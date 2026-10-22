#!/usr/bin/env node
// npm と NuGet の「patch 更新」だけを検出して、作業ツリーへ適用する。
//
// **patch とは、同じ major.minor の中で、より新しい patch（安定版）のこと。** minor・major は上げない
// （互換が壊れ得るので、人が見る）。プレリリース版・非推奨（deprecated）の版は選ばない。
//
// 適用するもの
//   - package.json の dependencies / devDependencies / optionalDependencies の版（接頭辞 ^ ~ は保つ。overrides は触らない）
//     → `npm install --package-lock-only --ignore-scripts` でロックファイルを直す
//   - .csproj の PackageReference と、Directory.Packages.props の PackageVersion（版が 1 つの数字のものだけ）
//     → `dotnet restore --force-evaluate` でロックファイルを直す（プロジェクトをまたいで依存が変わるため）
//   - scripts/notice-check.sh があれば、NOTICE の版を追随させる（直接依存の版が NOTICE と食い違うと落ちる検査）
//
// 使い方
//   node scripts/apply-patch-updates.mjs            適用する
//   node scripts/apply-patch-updates.mjs --dry-run  検出して表示するだけ（ファイルを書き換えない）
//
// 出力
//   - 標準出力に、更新の一覧
//   - patch-updates-summary.md（PR の本文に使う。--dry-run では作らない）
//   - GITHUB_OUTPUT があれば changed=true|false
//
// 無視する依存は、.github/patch-updates-ignore.txt に 1 行 1 名で書く（# から行末はコメント）
//
// 終了コード: 0=成功（更新なしを含む）、1=実行の失敗、2=更新したが NOTICE が直せなかった（PR を作らずに止める）
import { execFileSync, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

const root = path.resolve(path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1')), '..');
const dryRun = process.argv.includes('--dry-run');
process.chdir(root);

const SEMVER = /^(\d+)\.(\d+)\.(\d+)$/; // 安定版だけ（-rc.1 などは含めない）

function log(message) {
  console.log(message);
}

function git(...args) {
  return execFileSync('git', args, { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 }).split('\n').filter(Boolean);
}

function parse(version) {
  const m = SEMVER.exec(version);
  return m ? { major: Number(m[1]), minor: Number(m[2]), patch: Number(m[3]) } : null;
}

/** 現在の版と同じ major.minor で、より新しい patch のうち、最も高い安定版。無ければ null。 */
function highestPatch(current, candidates) {
  const cur = parse(current);
  if (!cur) {
    return null;
  }
  let best = null;
  for (const candidate of candidates) {
    const v = parse(candidate);
    if (v && v.major === cur.major && v.minor === cur.minor && v.patch > cur.patch && (!best || v.patch > best.v.patch)) {
      best = { v };
    }
  }
  // レジストリの文字列をそのまま使わず、検査済みの数字から版を作り直す（外部の入力をファイルへ流さない）
  return best ? `${best.v.major}.${best.v.minor}.${best.v.patch}` : null;
}

const ignore = new Set(
  fs.existsSync('.github/patch-updates-ignore.txt')
    ? fs.readFileSync('.github/patch-updates-ignore.txt', 'utf8')
        .split(/\r?\n/)
        .map((line) => line.replace(/#.*/, '').trim())
        .filter(Boolean)
    : [],
);

async function fetchJson(url, headers = {}) {
  for (let attempt = 1; attempt <= 3; attempt += 1) {
    const response = await fetch(url, { headers });
    if (response.ok) {
      return response.json();
    }
    if (response.status === 404) {
      return null;
    }
    if (attempt === 3) {
      throw new Error(`${url} が ${response.status} を返しました`);
    }
    await new Promise((resolve) => setTimeout(resolve, attempt * 2000));
  }
  return null;
}

const npmCache = new Map();
// パッケージ名・ID は、ファイルから読んだ値をリクエストの URL に使うので、使える文字だけに絞る
const SAFE_NAME = /^[A-Za-z0-9@._~/-]+$/;

async function npmVersions(name) {
  if (!SAFE_NAME.test(name)) {
    return [];
  }
  if (!npmCache.has(name)) {
    const doc = await fetchJson(`https://registry.npmjs.org/${name.replaceAll('/', '%2F')}`, {
      Accept: 'application/vnd.npm.install-v1+json',
    });
    const versions = doc?.versions ?? {};
    // 非推奨（deprecated）の版は選ばない
    npmCache.set(name, Object.keys(versions).filter((v) => !versions[v].deprecated));
  }
  return npmCache.get(name);
}

const nugetCache = new Map();
async function nugetVersions(id) {
  if (!SAFE_NAME.test(id)) {
    return [];
  }
  const key = id.toLowerCase();
  if (!nugetCache.has(key)) {
    const doc = await fetchJson(`https://api.nuget.org/v3-flatcontainer/${key}/index.json`);
    nugetCache.set(key, doc?.versions ?? []);
  }
  return nugetCache.get(key);
}

const updates = []; // { ecosystem, name, from, to, file }

// ---- npm ---------------------------------------------------------------------------------------
async function updateNpm() {
  const manifests = git('ls-files', '--', '*package.json').filter(
    (f) => !/(^|\/)(node_modules|_reference|temp)\//.test(f) && fs.existsSync(path.join(path.dirname(f), 'package-lock.json')),
  );
  const dirty = [];
  for (const file of manifests) {
    const raw = fs.readFileSync(file, 'utf8');
    const eol = raw.includes('\r\n') ? '\r\n' : '\n';
    const text = raw.replace(/\r\n/g, '\n');
    const indent = /^(\s+)"/m.exec(text)?.[1] ?? '  ';
    const manifest = JSON.parse(text);
    let changed = false;
    for (const section of ['dependencies', 'devDependencies', 'optionalDependencies']) {
      for (const [name, spec] of Object.entries(manifest[section] ?? {})) {
        const m = /^([\^~]?)(\d+\.\d+\.\d+)$/.exec(spec);
        if (!m || ignore.has(name)) {
          continue;
        }
        const next = highestPatch(m[2], await npmVersions(name));
        if (next) {
          updates.push({ ecosystem: 'npm', name, from: m[2], to: next, file });
          manifest[section][name] = `${m[1]}${next}`;
          changed = true;
        }
      }
    }
    if (changed && !dryRun) {
      fs.writeFileSync(file, JSON.stringify(manifest, null, indent).replace(/\n/g, eol) + eol);
      dirty.push(path.dirname(file));
    }
  }
  for (const dir of dirty) {
    log(`npm install --package-lock-only (${dir})`);
    const result = spawnSync('npm', ['install', '--package-lock-only', '--ignore-scripts', '--no-audit', '--no-fund'], {
      cwd: dir,
      stdio: 'inherit',
      shell: process.platform === 'win32',
    });
    if (result.status !== 0) {
      throw new Error(`npm install --package-lock-only が失敗しました: ${dir}`);
    }
  }
}

// ---- NuGet -------------------------------------------------------------------------------------
async function updateNuget() {
  const files = git('ls-files', '--', '*.csproj', '*Directory.Packages.props').filter((f) => !/(^|\/)(_reference|temp)\//.test(f));
  let changedAny = false;
  for (const file of files) {
    const raw = fs.readFileSync(file, 'utf8');
    let text = raw;
    const pattern = /(<(?:PackageReference|PackageVersion)\b[^>]*?\bInclude="([^"]+)"[^>]*?\bVersion=")(\d+\.\d+\.\d+)(")/g;
    const found = [...raw.matchAll(pattern)];
    for (const m of found) {
      const id = m[2];
      if (ignore.has(id)) {
        continue;
      }
      const next = highestPatch(m[3], await nugetVersions(id));
      if (next) {
        updates.push({ ecosystem: 'NuGet', name: id, from: m[3], to: next, file });
        text = text.replace(m[0], `${m[1]}${next}${m[4]}`);
      }
    }
    if (text !== raw) {
      changedAny = true;
      if (!dryRun) {
        fs.writeFileSync(file, text);
      }
    }
  }
  if (changedAny && !dryRun) {
    const solution = fs.readdirSync('.').find((f) => /\.(slnx|sln)$/.test(f));
    if (!solution) {
      throw new Error('ソリューションが見つかりません（dotnet restore を実行できません）');
    }
    log(`dotnet restore ${solution} --force-evaluate`);
    const result = spawnSync('dotnet', ['restore', solution, '--force-evaluate'], { stdio: 'inherit', shell: process.platform === 'win32' });
    if (result.status !== 0) {
      throw new Error('dotnet restore --force-evaluate が失敗しました');
    }
  }
}

// ---- NOTICE ------------------------------------------------------------------------------------
function escapeRegex(text) {
  return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function noticeCheck() {
  const result = spawnSync('bash', ['scripts/notice-check.sh'], { encoding: 'utf8' });
  return { ok: result.status === 0, output: `${result.stdout ?? ''}${result.stderr ?? ''}` };
}

function fixNotice() {
  if (!fs.existsSync('scripts/notice-check.sh')) {
    return true;
  }
  let raw;
  try {
    raw = fs.readFileSync('NOTICE', 'utf8');
  } catch {
    return true;
  }
  let check = noticeCheck();
  if (check.ok) {
    return true;
  }
  const eol = raw.includes('\r\n') ? '\r\n' : '\n';
  let text = raw.replace(/\r\n/g, '\n');
  for (const m of check.output.matchAll(/^\s*NG\s+(\S+)\s+(\S+)\s+が NOTICE に見当たらない/gm)) {
    const [, name, version] = m;
    const lineStart = new RegExp(`^(\\s*)${escapeRegex(name)}\\s+[0-9][^\\s/]*(\\s+[/-]\\s)`, 'm');
    const inline = new RegExp(`(?<![\\w@/.-])${escapeRegex(name)}\\s+[0-9][^\\s/,、）)]*(\\s+[/-]\\s)`);
    if (lineStart.test(text)) {
      text = text.replace(lineStart, (_, lead, sep) => `${lead}${name} ${version}${sep}`);
    } else if (inline.test(text)) {
      text = text.replace(inline, (_, sep) => `${name} ${version}${sep}`);
    }
  }
  fs.writeFileSync('NOTICE', text.replace(/\n/g, eol));
  check = noticeCheck();
  if (!check.ok) {
    console.error(check.output);
  }
  return check.ok;
}

// ---- 実行 --------------------------------------------------------------------------------------
await updateNpm();
await updateNuget();

if (updates.length === 0) {
  log('patch 更新はありません。');
  if (process.env.GITHUB_OUTPUT) {
    fs.appendFileSync(process.env.GITHUB_OUTPUT, 'changed=false\n');
  }
  process.exit(0);
}

log(`patch 更新: ${updates.length} 件${dryRun ? '（--dry-run: 書き換えていません）' : ''}`);
for (const u of updates) {
  log(`  ${u.ecosystem.padEnd(5)} ${u.name} ${u.from} -> ${u.to}  (${u.file})`);
}

if (dryRun) {
  process.exit(0);
}

const noticeOk = fixNotice();

const lines = ['npm と NuGet の patch 更新（同じ major.minor の最新の安定版）を、自動で適用しました。', ''];
lines.push('| 種別 | パッケージ | 旧 | 新 | 場所 |', '| --- | --- | --- | --- | --- |');
for (const u of updates) {
  lines.push(`| ${u.ecosystem} | \`${u.name}\` | ${u.from} | ${u.to} | \`${u.file}\` |`);
}
lines.push('', 'minor・major の更新は含みません（人が見ます）。CI が通れば自動でマージされます。');
fs.writeFileSync('patch-updates-summary.md', `${lines.join('\n')}\n`);

if (process.env.GITHUB_OUTPUT) {
  fs.appendFileSync(process.env.GITHUB_OUTPUT, `changed=true\ncount=${updates.length}\n`);
}
if (!noticeOk) {
  console.error('NOTICE の整合検査が通りません。NOTICE を手で直してください（PR は作りません）。');
  process.exit(2);
}
