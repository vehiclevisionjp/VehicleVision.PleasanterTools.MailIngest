// Markdown の検査。markdownlint の API を直接使う（CLI の markdownlint-cli2 は使わない）。
//
// ■ なぜ CLI ではなく API か
//   markdownlint-cli2 は glob の依存（micromatch → braces）を引き連れる。braces には
//   修正版の無い脆弱性（GHSA-vfj7-8cjw-p6xm、全版が対象。2026-10-19 参照）があり、npm audit が落ちる。
//   API だけなら、その依存を持ち込まずに済む。**全リポジトリでこのファイルを同一に保つ。**
//
// ■ 設定
//   リポジトリごとに違うのは `.markdownlint-cli2.jsonc` だけ（VS Code 拡張と同じ書式。コメント可）。
//     config   markdownlint の規則設定
//     globs    対象を絞る（省略時は全 .md）
//     ignores  除外
//     checkLinks   false でリンク先の実在検査を止める（既定は検査する。ビルドで生成するファイルを指す場合など）
//     customRules  独自の規則（モジュールのパス。オブジェクトか、その配列を export する）
//
// ■ 対象
//   Git が追跡している .md ＋ 追跡していないが無視されていない .md。サブモジュール
//   （_reference）・node_modules・一時成果物は、Git の側で除かれる。
//
// ■ 使い方
//   node scripts/markdownlint.mjs          検査（違反があれば 1 で終わる）
//   node scripts/markdownlint.mjs --fix    直せるものを直す
import { execFileSync } from 'node:child_process';
import { readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { lint } from 'markdownlint/promise';
import { applyFixes } from 'markdownlint';

// JSONC（コメントと末尾のカンマ）を JSON にする。文字列の中は触らない。
function parseJsonc(text) {
  let out = '';
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (c === '"') {
      let j = i + 1;
      while (text[j] !== '"') j += text[j] === '\\' ? 2 : 1;
      out += text.slice(i, j + 1);
      i = j;
    } else if (c === '/' && text[i + 1] === '/') {
      while (i < text.length && text[i] !== '\n') i++;
      out += '\n';
    } else if (c === '/' && text[i + 1] === '*') {
      i = text.indexOf('*/', i + 2) + 1;
    } else {
      out += c;
    }
  }
  return JSON.parse(out.replace(/,(\s*[}\]])/g, '$1'));
}

// glob を正規表現にする。対応するのは `**`（階層をまたぐ）と `*`（1 階層内）だけ。
function globToRegExp(glob) {
  let re = '';
  for (let i = 0; i < glob.length; i++) {
    const c = glob[i];
    if (c === '*' && glob[i + 1] === '*') {
      i++;
      if (glob[i + 1] === '/') { i++; re += '(?:.*/)?'; } else { re += '.*'; }
    } else if (c === '*') {
      re += '[^/]*';
    } else {
      re += c.replace(/[.+^${}()|[\]\\?]/g, '\\$&');
    }
  }
  return new RegExp(`^${re}$`);
}

const settings = parseJsonc(await readFile('.markdownlint-cli2.jsonc', 'utf8'));
const config = settings.config ?? {};
const includes = (settings.globs ?? []).map(globToRegExp);
const ignores = (settings.ignores ?? []).map(globToRegExp);

const listed = execFileSync(
  'git', ['ls-files', '-z', '--cached', '--others', '--exclude-standard', '--', '*.md'],
  { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 },
).split('\0').filter(Boolean);
const customRules = [];
for (const rule of settings.customRules ?? []) {
  const loaded = await import(pathToFileURL(path.resolve(rule)).href);
  customRules.push(...[loaded.default ?? loaded].flat());
}
const files = [...new Set(listed)]
  .filter(file => !ignores.some(re => re.test(file)))
  .filter(file => includes.length === 0 || includes.some(re => re.test(file)))
  .sort();

let results = await lint({ files, config, customRules });
if (process.argv.includes('--fix')) {
  for (const file of files) {
    if (results[file].length) await writeFile(file, applyFixes(await readFile(file, 'utf8'), results[file]), 'utf8');
  }
  results = await lint({ files, config, customRules });
}

let failed = false;
for (const [file, problems] of Object.entries(results)) {
  for (const problem of problems) {
    failed = true;
    console.error(`${file}:${problem.lineNumber} ${problem.ruleNames[0]} ${problem.ruleDescription}${problem.errorDetail ? ': ' + problem.errorDetail : ''}`);
  }
}

// リポジトリ内へのリンクは、ファイルの実在も検査する（コードブロックとインラインコードの中は見ない）。
for (const file of settings.checkLinks === false ? [] : files) {
  const text = (await readFile(file, 'utf8')).replace(/^(```|~~~)[^\n]*\n[\s\S]*?^\1[^\n]*$/gm, '').replace(/`[^`\n]*`/g, '');
  for (const match of text.matchAll(/\]\(([^)\s]+)[^)]*\)/g)) {
    const target = decodeURI(match[1].split('#')[0].split('?')[0]);
    if (!target || /^([a-z][a-z0-9+.-]*:|\/|<)/i.test(target)) continue;
    try { await readFile(path.resolve(path.dirname(file), target)); } catch (error) {
      if (error.code === 'EISDIR') continue;
      failed = true;
      console.error(`${file}: リンク先が存在しない: ${target}`);
    }
  }
}

if (failed) process.exitCode = 1;
else console.log(`Markdown lint: ${files.length} files, 0 errors`);
