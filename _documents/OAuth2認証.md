# OAuth2 認証（設計案）

<!-- markdownlint-configure-file {"MD013": {"line_length": 120, "tables": false}} -->

## 1. 状態・目的・範囲

**レビュー用の設計文書（2026-11-06、Issue #90。親は #65、その親は #60）。実装（コード）はまだ書かない。**
本文の「推奨」は、第 11 節の質問に答えるまでは承認待ちの案であり、確定仕様ではない。
**実機では何も確かめていない。** 外部仕様は第 2 節の一次情報の記述にとどまり、
MailKit 以外は、接続して動かしたものはない。確認できなかった点は「未確認」と書く。

目的は、IMAP / POP3 の認証に OAuth2（SASL XOAUTH2）を足し、Microsoft 365（Exchange Online）と Gmail から、
パスワード認証なしでメールを取り込めるようにすること。
[アーキテクチャ方針](アーキテクチャ方針.md) の第 3 節のとおり、Microsoft 365 は Basic 認証が無効化されており
（第 2 節）、パスワード認証だけでは主な利用環境を扱えない。

### 決定済みの事項（ユーザー決定、2026-11-06。#65 のコメント）

| 質問 | 決定 |
| --- | --- |
| 8-1 トークンの保存 | 更新トークンは状態 DB（SQLite。Core.Data）に**暗号化して保存**し、ツールが自動で更新する。最初の認可は、運用者が専用のコマンドで 1 回だけ行う。暗号化の鍵は、既存の秘密の渡し方（環境変数または `_FILE`）で別に渡す。Microsoft の更新トークンは更新のたびに新しい値が返ることがあるため、書き戻しを前提にする |
| 8-2 対象 | Microsoft 365 と Gmail を同時に作る。PR は、共通の部分・Microsoft 365・Gmail に分けてよい |
| 8-3 設定の形 | `authentication.mode` に `oauth2` を足し、`allowedMechanisms` は `password` と同じ形で `XOAUTH2` を明示する。`provider` は `microsoft365`・`gmail`。既存の原則（設定に書いた方式だけを使う・フォールバックしない・1 つの source は 1 つの認証方式）を変えない |

この文書は、決定の**細部**（項目名・表・コマンド・失敗時の挙動）を示し、決めきれない点を第 11 節で聞く。

### 範囲

| 区分 | 内容 |
| --- | --- |
| 範囲内 | 委任（ユーザーとしての）認可コードフロー、更新トークンの暗号化保存と自動更新、認可・状態確認のコマンド、起動前検査、許可リストとの整合 |
| 後続（Q 次第） | Microsoft 365 のアプリ専用（クライアント資格情報）、Microsoft のデバイスコードフロー、鍵の交換コマンド、管理者への通知 |
| 対象外（初版） | Gmail のサービスアカウント（ドメイン全体の委任）、個人の Microsoft アカウント（outlook.com）、SMTP 送信、IMAP / POP3 以外 |

## 2. 一次情報の確認（参照日 2026-11-06）

出典は参照日 2026-11-06 に取得した。**取得は、本文の要約を返す取得ツールを経由した。**
Microsoft Learn と GitHub の MailKit 文書は原文（メタ情報付き）を確認した。Google の各ページは要約の結果のため、
**数値・規則は要約に基づく**。実装時に、原文で再確認する（第 10 節）。
記憶で書いた事実は無い。確認できなかったものは「未確認」とする。

### 2.1 Microsoft 365（Exchange Online）

| 項目 | 確認した内容 | 出典 |
| --- | --- | --- |
| Basic 認証 | Exchange Online の POP・IMAP などで、Basic 認証は**すべてのテナントで無効**。アプリは OAuth 2.0（先進認証）へ移る必要がある。アプリパスワードも使えない | [Basic 認証の廃止][ms-basic]（ms.date 2026-07-10） |
| IMAP / POP の OAuth | 2020 年に対応。XOAUTH2 の SASL 形式で、アクセストークンを送る。Microsoft 365 と Outlook.com が対象 | [IMAP・POP・SMTP を OAuth で認証する][ms-imap-oauth]（ms.date 2021-09-21、更新 2025-10-17） |
| XOAUTH2 の形式 | `base64("user=" + ユーザー + ^A + "auth=Bearer " + アクセストークン + ^A^A)`。`^A` は 0x01。共有メールボックスは、ユーザー名の欄を共有メールボックスのアドレスに置き換える（トークンは、アクセス権のあるユーザーのもの） | 同上 |
| 委任のスコープ | IMAP は `https://outlook.office.com/IMAP.AccessAsUser.All`、POP は `https://outlook.office.com/POP.AccessAsUser.All`。更新トークンを得るには `offline_access` も要る | 同上 |
| 認可の方式 | 認可コードフローと、デバイス認可グラントの両方が、取得手段として挙げられている。クライアント資格情報フローも使える | 同上 |
| アプリ専用（クライアント資格情報） | アプリ登録の API のアクセス許可に、Office 365 Exchange Online の**アプリケーションの許可**（`IMAP.AccessAsApp`・`POP.AccessAsApp`）を足し、テナント管理者が同意する。Exchange Online PowerShell で `New-ServicePrincipal` により、サービスプリンシパルを Exchange へ登録し、`Add-MailboxPermission -AccessRights FullAccess` で、**メールボックスごとに**許可する。トークンの要求では `https://outlook.office365.com/.default` を使う。**`New-ServicePrincipal` の `ObjectId` は、エンタープライズアプリケーション側のオブジェクト ID**（アプリの登録側ではない。取り違えると認証に失敗する） | 同上 |
| 管理者の同意の URL | 複数テナント向けの登録では、IMAP・POP のアプリケーションの許可の同意に `https://ps.outlook.com/.default` を使う（SMTP は `https://outlook.office365.com/.default`）。MailKit の文書も、クライアント資格情報で IMAP・POP は `https://ps.outlook.com/.default`、SMTP は `https://outlook.office365.com/.default` と書く。上の「トークンの要求は `outlook.office365.com/.default`」と食い違って見えるため、**どちらが IMAP の実トークンに要るかは未確認** | 同上、[MailKit の Exchange の OAuth2 の文書][mailkit-exchange] |
| 更新トークンの寿命 | 既定は、SPA は 24 時間、メールのワンタイムパスコード認証は 24 時間、**それ以外は 90 日**。使うたびに**新しい更新トークンへ置き換わる**。**古い更新トークンは、使っても取り消されない**ため、新しいものを得たら古いものを安全に削除する。更新トークンは、アクセストークンやアプリの資格情報と同じく安全に保管する | [更新トークン][ms-refresh]（ms.date 2025-11-05） |
| 更新トークンの失効 | 期限切れのほか、いつでもサービスが取り消し得る。パスワードの変更（ユーザー・SSPR・管理者のリセット）、ユーザーまたは管理者による取り消しで失効する場合がある。**クライアントの種類（公開か機密か）とサインインの方法で、失効の有無が違う**表がある。このツールの更新トークンがどの列に当たるかは、実機で確かめていない（**未確認**） | 同上 |
| 更新の要求 | `grant_type=refresh_token`。新しい `refresh_token` が応答に入る（`offline_access` を要求したとき）。「古いものを捨て、新しいものに置き換える」。OAuth 2.0 の仕様は、サーバーが新しい更新トークンを発行してもよく、そのとき古いものを取り消してもよい、と定める。**機密クライアント（Web アプリ）は更新でもクライアントシークレットが要る。ネイティブアプリでは使わない** | [認可コードフロー][ms-authcode]（ms.date 2026-01-09） |
| 認可コードフロー | 認可コードフローは PKCE と併用する。`code_challenge` は全種類のアプリで推奨。方式は `S256` を推奨（`plain` も可）。`state` は CSRF 対策で推奨、応答の値を照合する。認可コードは短命（約 1 分）。公開クライアント（ネイティブ・SPA）は、コードの引き換えにシークレットや証明書を使ってはならない | 同上 |
| リダイレクト URI | ネイティブアプリは、システムブラウザなら `http://localhost`。`http` は localhost だけ許される。**localhost のポートは照合で無視される**（毎回違うポートでよい）。**IPv6 の `[::1]` は現在未対応**。`127.0.0.1` の使用が勧められるが、ポータルの入力欄では `http` の `127.0.0.1` を足せず、マニフェストの編集が要る | [リダイレクト URI][ms-reply]（ms.date 2025-05-14） |
| デバイスコード | `/devicecode` で `user_code` と `verification_uri` を得て、ユーザーが別の端末のブラウザで入力する。クライアントは `/token` をポーリング。既定の有効時間 15 分（`expires_in`）。エラーは `authorization_pending`・`authorization_declined`・`expired_token`・`bad_verification_code`。リダイレクト URI は要らない。`refresh_token` は `offline_access` のとき得られる。`verification_uri_complete` は未対応 | [デバイス認可グラント][ms-device]（ms.date 2025-01-04） |
| IMAP / POP の有効化 | Exchange Online では、ユーザーのメールボックスの作成時に、IMAP4・POP3 は**既定で有効**。メールボックスごとに、Exchange 管理センターの「メール アプリ」で、IMAP・POP3 を有効・無効にできる（PowerShell は `Set-CASMailbox`。**IMAP 用のパラメーター名は、取得したページに例が無く未確認**）。テナント全体の設定は**未確認** | [ユーザーのメールボックスのメール アプリ][ms-apps]（ms.date 2024-05-17） |
| ホスト名・ポート | 接続設定の案内ページへのリンクのみ。`outlook.office365.com` の IMAP 993・POP 995 は、**今回は一次情報の本文で確認していない（未確認）** | [IMAP・POP・SMTP を OAuth で認証する][ms-imap-oauth] |
| アクセストークンの寿命 | 文書の応答例は `expires_in` が 3599 秒。**既定の寿命の規定は、今回は確認していない（未確認）** | [認可コードフロー][ms-authcode] |
| クライアント資格情報に更新トークンが無いこと | クライアント資格情報フローの応答に更新トークンが含まれるかは、**今回の取得では確認していない（未確認。OAuth 2.0 の一般的な理解では含まれないが、一次情報では確かめていない）** | — |

[ms-basic]: https://learn.microsoft.com/en-us/exchange/clients-and-mobile-in-exchange-online/deprecation-of-basic-authentication-exchange-online
[ms-imap-oauth]: https://learn.microsoft.com/en-us/exchange/client-developer/legacy-protocols/how-to-authenticate-an-imap-pop-smtp-application-by-using-oauth
[ms-refresh]: https://learn.microsoft.com/en-us/entra/identity-platform/refresh-tokens
[ms-authcode]: https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-auth-code-flow
[ms-reply]: https://learn.microsoft.com/en-us/entra/identity-platform/reply-url
[ms-device]: https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-device-code
[ms-apps]: https://learn.microsoft.com/en-us/exchange/clients-and-mobile-in-exchange-online/pop3-and-imap4/enable-or-disable-pop3-or-imap4-access

### 2.2 Gmail

| 項目 | 確認した内容 | 出典 |
| --- | --- | --- |
| XOAUTH2 | 初期クライアント応答は、`user=<ユーザー>` ^A `auth=Bearer <アクセストークン>` ^A^A を base64 にしたもの。スコープは IMAP・POP・SMTP とも `https://mail.google.com/`。失敗時は、サーバーが base64 の JSON（`status`・`schemes`・`scope`）を含むチャレンジを返し、クライアントは空の応答を返し、サーバーが最終の失敗を返す（IMAP は `NO SASL authentication failed`） | [Gmail の XOAUTH2 プロトコル][g-xoauth2]（更新日の記載なし） |
| ドメイン全体の委任 | Google Workspace のドメイン全体の委任でサービスアカウントを使う場合に限り、IMAP は `https://www.googleapis.com/auth/gmail.imap_admin` も使える（他の用途では動かない）。**初版の対象外**。トークンの取得手順は**今回は確認していない（未確認）** | 同上 |
| ホスト名・ポート | XOAUTH2 の文書には記載なし。MailKit の文書の例は `imap.gmail.com` の 993（暗黙 TLS） | [MailKit の GMail の OAuth2 の文書][mailkit-gmail] |
| スコープの区分 | `https://mail.google.com/` は**制限付き（Restricted）スコープ**。制限付きスコープのデータを、サーバー経由で保存・送信するアプリは、Google が承認した評価者による**セキュリティ評価**（CASA）が要り、**12 か月ごと**に再評価される。免除は、個人利用（本人と、個人的に知る少数）・開発やテスト・サービスアカウントが自分のデータだけ・**組織内だけの利用（ユーザーの種類が Internal）** など | [Gmail API のスコープ][g-scopes]、[制限付きスコープの審査][g-restricted]（更新日の記載なし） |
| 同意画面 | ユーザーの種類は External（誰でも）と Internal（Google Cloud の組織のメンバーだけ。外は `org_internal`）。公開状態が **Testing** のときは、テストユーザー最大 100 人で、**同意から 7 日で認可と更新トークンが失効**する（氏名・メール・プロフィールだけの要求は除く）。In production でも、審査を受けていないアプリで機密・制限付きスコープを使うと、「未確認のアプリ」の警告が出て、新規ユーザー数が**生涯で 100 人**に制限される | [同意画面の設定][g-consent]（更新日の記載なし） |
| 更新トークンの失効 | ユーザーがアクセスを取り消した、**6 か月使われなかった**、**Gmail のスコープを含むトークンはパスワード変更で失効**、ユーザーのアカウントの生きた更新トークンが上限を超えた、管理者のポリシー（`admin_policy_enforced`）、管理者のセッション長（`invalid_grant`）、Testing の 7 日。**1 アカウント・1 クライアント ID あたり 100 個**で、超えると**最も古いものが警告なしで無効**になる | [OAuth 2.0 の概要][g-oauth] |
| クライアントの種類 | Android・iOS・デスクトップアプリ・Web アプリ・Chrome 拡張・TV と入力が限られるデバイス・サービスアカウント | 同上 |
| デスクトップアプリの認可 | ループバック IP（`http://127.0.0.1:ポート`・`http://[::1]:ポート`）が推奨。`localhost` も使えるがファイアウォールで止まることがある。**PKCE は必ず使う**（`S256` 推奨）。`state` は CSRF 対策として推奨。デスクトップアプリは公開クライアントで、クライアントシークレットはトークン交換・更新で**任意**。カスタム URI スキームと OOB は廃止 | [デスクトップアプリの OAuth 2.0][g-native] |
| デバイスフロー | **Gmail のスコープは対象外**。許されるのは、`email`・`openid`・`profile`・Drive の一部・YouTube だけ。`https://mail.google.com/` は一覧に無い。クライアントの種類は「TV と入力が限られるデバイス」。ポーリングの間隔は `interval`、エンドポイントは `https://oauth2.googleapis.com/device/code` と `https://oauth2.googleapis.com/token` | [TV と入力が限られるデバイスの OAuth 2.0][g-device] |
| 認可のエンドポイント・`access_type=offline` | **取得した要約に記載なし（未確認）。** 最初の同意で更新トークンが返る条件（`access_type=offline`・`prompt=consent` の要否）は、実装時に原文で確認する | — |
| アクセストークンの寿命 | **未確認** | — |
| 更新トークンが回転するか | **取得した資料に記載なし（未確認）。** 回転する場合に備え、Microsoft と同じ書き戻しの仕組みを使う | — |

[g-xoauth2]: https://developers.google.com/workspace/gmail/imap/xoauth2-protocol
[g-scopes]: https://developers.google.com/workspace/gmail/api/auth/scopes
[g-restricted]: https://developers.google.com/identity/protocols/oauth2/production-readiness/restricted-scope-verification
[g-consent]: https://support.google.com/cloud/answer/15549945
[g-oauth]: https://developers.google.com/identity/protocols/oauth2
[g-native]: https://developers.google.com/identity/protocols/oauth2/native-app
[g-device]: https://developers.google.com/identity/protocols/oauth2/limited-input-device

### 2.3 MailKit（採用版 4.18.1）

- 採用版 4.18.1（MIT）の XML ドキュメントで、`MailKit.Security.SaslMechanismOAuth2`（XOAUTH2。Google 用に作られた方式という説明）と
  `SaslMechanismOAuthBearer`（OAUTHBEARER）の両方を確認した。どちらも `(ユーザー名, アクセストークン)` のコンストラクターがある。
  NuGet のキャッシュにある `lib/net10.0/MailKit.xml`（参照日 2026-11-06）。
- 認証は、`client.AuthenticateAsync(SaslMechanism)` に、方式を**直接渡す**。方式を渡すので、MailKit の自動選択（SASL → APOP → USER/PASS）を通らない。
  これは、「許可した方式 1 つだけ・フォールバックなし」の原則に合う。
- [MailKit の Exchange の OAuth2 の文書][mailkit-exchange]（GitHub master、参照日 2026-11-06）は `SaslMechanismOAuth2` を使い、
  [GMail の文書][mailkit-gmail] は `SaslMechanismOAuthBearer` を使う。**Gmail が OAUTHBEARER を広告し、XOAUTH2 も広告するかは、実機で確かめていない（未確認）**。
  決定 8-3 は `XOAUTH2` なので、本文は XOAUTH2 を前提にし、Gmail で食い違った場合の扱いを Q5 で聞く。
- MailKit の文書は、トークンの取得に MSAL（Microsoft）と Google.Apis.Auth（Google）を使う。本ツールが同じものを使うかは Q2。

[mailkit-exchange]: https://github.com/jstedfast/MailKit/blob/master/ExchangeOAuth2.md
[mailkit-gmail]: https://github.com/jstedfast/MailKit/blob/master/GMailOAuth2.md

### 2.4 一次情報から導いた運用上の注意

- **更新トークンは、ある日突然失効する**: パスワード変更（Microsoft の公開クライアントの一部、Gmail のスコープ）、管理者の取り消し、
  長期間の未使用（Microsoft の既定 90 日、Google は 6 か月）、Google の Testing（7 日）。
  再認可の手順（第 4 節）と、失効を運用者に気づかせる手段（第 8 節）が要る。
- **Microsoft 365 は、アプリ専用のほうが無人運用に向く可能性がある**: 更新トークンもパスワード変更の影響も無いが、確認できなかった点（上の表）がある。Q4。
- **Gmail の `https://mail.google.com/` は狭められない**（IMAP の通常の利用では唯一のスコープ）。権限の最小化は、Microsoft（メールボックスごとの許可）ほど効かない。
- 個人の Gmail で使うなら、In production・未審査・警告つきの運用になる。Workspace なら、Internal にすれば審査が免除される（2.2 の免除の記述）。

## 3. 認可の方式（推奨を 1 つ）

### 3.1 比較

| 観点 | 認可コード + PKCE + ループバック | デバイスコード |
| --- | --- | --- |
| Microsoft 365 | 対応（`http://localhost`、ポート無視。`[::1]` は未対応） | 対応 |
| Gmail | 対応（`127.0.0.1` を推奨） | **Gmail のスコープは対象外**（2.2）。使えない |
| 画面の無いサーバー | ブラウザと同じ端末でしか、リダイレクトを受けられない。SSH のポート転送か、URL の貼り付けで補う | 向く（別の端末のブラウザで足りる） |
| 安全性 | PKCE と `state` で、コードの横取りと CSRF を防ぐ | ユーザーコードを他人に入力させる誘導（フィッシング）の余地。PKCE の代わりは無い |
| 実装の量 | リスナーと PKCE・`state` が要る | ポーリングだけ |

### 3.2 推奨

**認可コードフロー（PKCE の `S256`・`state`・ループバックのリダイレクト）を、両プロバイダー共通の唯一の方式にする。**

理由:

1. Gmail は、デバイスコードで IMAP のスコープを使えない（2.2）。決定 8-2 は 2 つを同時に作るので、共通の方式が要る。方式を 2 本持つと、試験と未検証の範囲が増える。
2. 公開クライアントでシークレットを持たずに済み（Microsoft）、PKCE が必須の Google とも整合する。
3. 画面の無いサーバーの問題は、運用者の端末側で補える（第 4 節の `--port` と SSH のポート転送、または `--paste`）。サーバーにブラウザは要らない。
4. 認可は、運用者が 1 回だけ行う作業（決定 8-1）。使いやすさの代償は、最初の 1 回の手順の手間だけ。

Microsoft のデバイスコードは、Microsoft 365 だけの補助として後続にできる。初版に入れるかは Q1 で聞く。

### 3.3 クライアントの種類

| プロバイダー | 種類 | シークレット |
| --- | --- | --- |
| Microsoft 365 | アプリの登録で「モバイルとデスクトップ アプリケーション」に `http://localhost` を登録し、**公開クライアントフローを許可**する（公開クライアント） | **持たない**（公開クライアントはコードの引き換えにシークレットを使わない。2.1） |
| Gmail | 「デスクトップ アプリ」の OAuth クライアント | トークン交換・更新で**任意**（2.2）。要るかどうかは実機で確認。設定には**任意**の項目として置く |

アプリの登録・OAuth クライアントは、**運用者が自分のテナント・プロジェクトで作る**ことを推奨する（本ツールの配布者が共通のクライアント ID を配らない）。
Google の制限付きスコープの審査（2.2）を、配布者が背負わないため。Q9。

## 4. 設定 JSON の形

### 4.1 項目

`authentication` の `mode: "oauth2"` のときだけ、次の項目を使う。**`mode: "password"` の項目（`passwordEnvironmentVariable`・
`allowInsecurePasswordAuthentication`）との混在は起動エラー**（1 つの source は 1 つの認証方式）。

| 項目 | 必須 | 内容 |
| --- | --- | --- |
| `mode` | 必須 | `oauth2` |
| `provider` | 必須 | `microsoft365` または `gmail`。未知値は起動エラー |
| `allowedMechanisms` | 必須 | `["XOAUTH2"]` のみ。**省略不可**（`password` のように既定を補わない）。他の値・重複・空は起動エラー |
| `userEnvironmentVariable` | 必須 | メールボックスのアドレスの環境変数名。`password` と同じ規則（`NAME` / `NAME_FILE`）。XOAUTH2 の `user=` に使う |
| `oauth2.grant` | 必須 | `authorizationCode`（初版の唯一の値。後続の `clientCredentials` などの拡張点） |
| `oauth2.clientId` | 必須 | アプリの登録のクライアント ID。秘密ではない（設定 JSON に書いてよい） |
| `oauth2.tenant` | `microsoft365` で必須、`gmail` で禁止 | テナント ID（GUID）またはドメイン名。`common`・`organizations`・`consumers` は不可（対象を 1 つのテナントに固定する）。URL の組み立てに使うため、形式を検査する（英数字・`-`・`.` だけ） |
| `oauth2.clientSecretEnvironmentVariable` | `microsoft365` で禁止、`gmail` で任意 | クライアントシークレットの環境変数名（`NAME` / `NAME_FILE`）。値は JSON に置かない |
| `oauth2.tokenKeyEnvironmentVariable` | 必須 | 更新トークンの暗号化の鍵の環境変数名（`NAME` / `NAME_FILE`）。Base64 の 32 バイト。複数の source で同じ名前を共有してよい |

**スコープは設定に書かせない。** プロバイダーとプロトコルから、コードが固定で決める（最小権限と設定ミスの防止）。

| `provider` | プロトコル | スコープ（固定） |
| --- | --- | --- |
| `microsoft365` | IMAP | `offline_access`、`https://outlook.office.com/IMAP.AccessAsUser.All` |
| `microsoft365` | POP3 | `offline_access`、`https://outlook.office.com/POP.AccessAsUser.All` |
| `gmail` | IMAP / POP3 | `https://mail.google.com/` |

認可と更新で使うエンドポイントも、**設定に書かせず、コードに固定**する（設定から外部へトークンを送らせない）。
Microsoft は `https://login.microsoftonline.com/{tenant}/oauth2/v2.0/{authorize|token}`（取得した文書の形）。
Google のトークンのエンドポイントは `https://oauth2.googleapis.com/token`（デバイスフローの文書に記載）。
**Google の認可のエンドポイントは、取得した要約に無く未確認**（実装時に原文で確認する）。

`tls` は `Tls` または `StartTls` のみ。`None` は起動エラー（第 7 節）。

### 4.2 例（Microsoft 365・IMAP）

```json
{
  "sourceId": "support-m365",
  "protocol": "imap",
  "host": "outlook.office365.com",
  "port": 993,
  "tls": "Tls",
  "authentication": {
    "mode": "oauth2",
    "provider": "microsoft365",
    "allowedMechanisms": ["XOAUTH2"],
    "userEnvironmentVariable": "MAILINGEST_SUPPORT_USER",
    "oauth2": {
      "grant": "authorizationCode",
      "clientId": "00000000-0000-0000-0000-000000000000",
      "tenant": "contoso.onmicrosoft.com",
      "tokenKeyEnvironmentVariable": "MAILINGEST_TOKEN_KEY"
    }
  }
}
```

### 4.3 例（Gmail・IMAP）

```json
{
  "sourceId": "support-gmail",
  "protocol": "imap",
  "host": "imap.gmail.com",
  "port": 993,
  "tls": "Tls",
  "authentication": {
    "mode": "oauth2",
    "provider": "gmail",
    "allowedMechanisms": ["XOAUTH2"],
    "userEnvironmentVariable": "MAILINGEST_GMAIL_USER",
    "oauth2": {
      "grant": "authorizationCode",
      "clientId": "example.apps.googleusercontent.com",
      "clientSecretEnvironmentVariable": "MAILINGEST_GMAIL_CLIENT_SECRET",
      "tokenKeyEnvironmentVariable": "MAILINGEST_TOKEN_KEY"
    }
  }
}
```

`host`・ポートは説明用。`outlook.office365.com` は、今回の一次情報では確認していない（2.1）。
プロバイダーと `host` が食い違う設定は、**警告**にとどめる（エラーにしない。社内の中継などを禁じない）。

### 4.4 秘密の渡し方

- 鍵・ユーザー名・クライアントシークレットは、既存の `SecretResolver`（`NAME` / `NAME_FILE`）の規則のとおり。
  同時指定は起動エラー、ファイルは絶対パス・UTF-8・64 KiB 以下・末尾の改行 1 個を除く、空・読めないものはエラー。
- 鍵は、解決後に Base64 として復号でき、**ちょうど 32 バイト**であること（満たさなければ起動エラー。値は出さない）。
  鍵の生成は、運用者が OS の道具（例: `openssl rand -base64 32`）で行う。本ツールは鍵を生成・保存・表示しない
  （Core の `SecretProtector` も「鍵の自動生成・保存・設定解決はしない」方針）。
- 更新トークンとアクセストークンを、設定 JSON・環境変数・コマンドの引数で受け取らない。

## 5. 認可コマンド（運用者が 1 回だけ行う）

### 5.1 コマンド

既存の CLI は、動詞を平らに並べている（`check`・`validate`・`run`・`migrate`・`migrate-status`）。同じ流儀で足す。

| コマンド | 内容 |
| --- | --- |
| `oauth2-authorize --config <path> --source <sourceId> [--port <n>] [--paste] [--open] [--no-verify] [--timeout <seconds>]` | 認可を行い、更新トークンを暗号化して保存する |
| `oauth2-status --config <path> [--source <sourceId>] [--json]` | 認可の状態を表示する（DB だけを読む。通信しない。トークンは出さない） |

`oauth2-forget`（保存したトークンの削除）は、初版に入れない案（再認可は上書きで足りる）。Q は立てず、実装の Issue で再検討する。

### 5.2 流れ

1. 設定を読み、対象の source が `mode: oauth2` であることを確かめる。鍵・ユーザー名・（Gmail の）クライアントシークレットを解決する（値は出さない）。
   状態 DB に未適用のマイグレーションがあれば止まる（`run` と同じ。`migrate` を先に行う）。
2. その source の**実行排他**（`SourceRuns`）を、所有者 `oauth2-authorize` で取る。`run` が実行中なら、取れず、何も書かずに止まる
   （更新トークンの書き込みを `run` と競合させないため。第 6 節）。
3. `state`（32 バイトの乱数の base64url）と、PKCE の `code_verifier`（43 文字以上の乱数）・`code_challenge`（`S256`）を作る。
4. 127.0.0.1 の**空きポート**（`--port` があればそれ）で、ループバックだけに束縛した待ち受けを開く。リダイレクト URI は、
   Microsoft は `http://localhost:{port}/`（アプリの登録は `http://localhost`。ポートは照合で無視される）、
   Google は `http://127.0.0.1:{port}/`。**IPv6 のループバックの扱い（`localhost` が `::1` に解決される環境）と、パス要素の要否は未確認**（実機で確かめる）。
5. 認可の URL を標準出力に出す（`client_id`・`state`・`code_challenge`・スコープだけで、秘密を含まない）。`--open` のときだけ、既定のブラウザで開く。
6. 運用者がブラウザで同意する。待ち受けは、**最初の正しい 1 回の要求だけ**を受ける（後述）。
7. 認可コードを、PKCE の `code_verifier` つきで、トークンのエンドポイントへ送る（Microsoft は公開クライアントでシークレットなし。Gmail は設定されていればシークレット）。
8. 応答に更新トークンが**無ければ失敗**（何も保存しない）。ある場合だけ、暗号化して保存する（第 6 節）。
9. 既定で、**認証の試験**を行う（`--no-verify` で省略）: 保存した更新トークンからアクセストークンを得て、メールサーバーへ接続し、XOAUTH2 で認証して切断する
   （メールは読まない）。スコープ・メールボックスの権限・IMAP の有効化の誤りを、その場で見つけるため。
10. 結果を表示して終わる。排他を解放する。

### 5.3 画面・出力（案。英語。秘密は出さない）

```text
Authorize source 'support-m365' (provider: microsoft365, protocol: imap)
Open this URL in a browser on a machine that can reach http://localhost:51234/ :

  https://login.microsoftonline.com/<tenant>/oauth2/v2.0/authorize?client_id=...&code_challenge=...&state=...

Waiting for the browser redirect (timeout 300 s). Press Ctrl+C to cancel.
Authorization code received. Exchanging for tokens...
Tokens saved (encrypted). Refresh token version: 1.
Mail server authentication test: succeeded (XOAUTH2).
```

出さないもの: 認可コード、アクセストークン、更新トークン、クライアントシークレット、鍵、ユーザー名（メールアドレス）、リダイレクトで受け取った URL の全体、トークンのエンドポイントの応答の本文。
`oauth2-status` の出力は、source ID・プロバイダー・状態・認可日時・最終更新日時・更新トークンの版・（検査の結果の）エラーコードだけ。

### 5.4 画面の無いサーバーでの手順

| 方法 | 手順 |
| --- | --- |
| SSH のポート転送（推奨） | サーバーで `oauth2-authorize ... --port 51234`。運用者の端末で `ssh -L 51234:127.0.0.1:51234 <server>` を張り、端末のブラウザで認可 URL を開く。リダイレクトは、端末の `localhost:51234` からトンネル経由で、サーバーの待ち受けに届く。ポートは、各端末のループバックにだけ開く |
| URL の貼り付け（`--paste`） | 待ち受けを開かない。別の端末のブラウザで認可し、**リダイレクト先（読み込みに失敗する）のアドレスバーの URL 全体**をコピーして、プロンプトに貼る（入力は画面にエコーしない。履歴に残さない）。ツールは、`state` を照合してコードを取り出す。コードは短命（Microsoft は約 1 分。2.1）なので急ぐ |

### 5.5 待ち受けの規則（安全）

- 束縛は、**127.0.0.1（と、使えれば `::1`）だけ**。`0.0.0.0` や全アドレスに束縛しない。
- 許す要求: HTTP の GET、パスがリダイレクト URI と一致、`Host` がループバックの名前、`state` が一致（定数時間の比較）。それ以外は、`400` / `404` を返して**無視**し、待ち続ける（期限まで）。
- 最初の正しい要求を受けたら、待ち受けを閉じる。1 回の認可につき、コードは 1 度しか使わない。
- 応答の HTML は、固定の短い文面（「このウィンドウを閉じてよい」）。受け取ったパラメーターを**表示しない**。スクリプトを含めない。`Cache-Control: no-store`・`Referrer-Policy: no-referrer`。
- 期限（既定 300 秒。`--timeout`）が来たら失敗。待ち受けの要求の URL を、ログ・例外に出さない（認可コードを含む）。
- 認可サーバーのエラー（`error=...`）は、**許可リストにある `error` のコード**だけを表示し、`error_description`（トレース ID などを含み得る）は表示しない。

### 5.6 失敗時

| 失敗 | 終了コード | 扱い |
| --- | --- | --- |
| 設定の不正、鍵・ユーザー名の未解決、DB 未適用、対象が `oauth2` でない | 2 | 何もしない。変数名・設定の位置だけを表示 |
| `run` が実行中（排他を取れない） | 1 | 何も書かない。`run` の終了を待つよう案内 |
| 期限切れ・`access_denied`・`state` 不一致の連続 | 1 | 何も保存しない。コードだけを表示 |
| トークンのエンドポイントの失敗（`invalid_grant`・`invalid_client`・通信失敗など） | 1 | 何も保存しない。コードだけを表示 |
| 応答に更新トークンが無い | 1 | 何も保存しない。`offline_access` / Google の条件（2.2、未確認）の確認を案内 |
| 保存の失敗（DB・暗号化） | 1 | 保存できていない。トークンは出さない |
| 保存したが、メールサーバーの認証の試験に失敗 | 1 | **保存は残す**（原因は、IMAP の無効・メールボックスの権限など。第 8 節の表）。試験の失敗であることを明示し、再実行で上書きできる |
| Ctrl+C | 1 | 待ち受けを閉じ、何も保存しない |

### 5.7 再認可（更新トークンの失効時）

`run` が `OAuth2ReauthorizationRequired` で source を止めたとき（第 8 節）、または `oauth2-status` が `ReauthorizationRequired` を示すとき:

1. （Microsoft・Google の側で原因を確かめる。パスワードの変更・管理者の取り消し・Testing の 7 日・長期間の未使用など。2.4）
2. 同じコマンド `oauth2-authorize --config <path> --source <sourceId>` を再実行する。**既存の行を上書きし、状態を `Active` に戻す**。
3. `oauth2-status` で `Active` を確かめ、次の `run` を待つ（取り込み待ちのメールは、メールサーバーに残っており、次回の実行で取り込まれる。#66 の既存の設計のとおり）。

## 6. トークンの保存と更新

### 6.1 状態 DB の表（案。マイグレーション `M0002`）

| 列 | 型（4 種の RDBMS で通じる型） | 内容 |
| --- | --- | --- |
| `SourceId` | 文字列 64、主キー | source との対応（1 source に 1 行） |
| `Provider` | 文字列 16 | `microsoft365` / `gmail` |
| `GrantFingerprint` | 文字列 64 | プロバイダー・テナント・クライアント ID・スコープ・メールボックスの正規化した連結の SHA-256（16 進）。**設定の変更を検出する**（秘密ではない。ただし、メールアドレスの存在を推測する材料になり得るため、ログ・サマリーに出さない） |
| `RefreshTokenProtected` | 長い文字列（上限の無い型） | 暗号化した更新トークン（版つきの書式。第 6.3 節）。長さの上限は、一次情報に無い（**未確認**）ため、`(MAX)` / `TEXT` 型を使う |
| `TokenVersion` | 整数 | 更新トークンの版。書き込みのたびに +1。**楽観的な排他**に使う（`UPDATE ... WHERE SourceId = @id AND TokenVersion = @expected`） |
| `Status` | 文字列 32 | `Active` / `ReauthorizationRequired` |
| `LastErrorCode` | 文字列 64、NULL 可 | 直近の失敗コード（秘密を含まない固定の文字列） |
| `AuthorizedAt` | 日時 | 最初の認可の時刻（再認可で更新） |
| `RefreshedAt` | 日時、NULL 可 | 最後に更新に成功した時刻 |
| `UpdatedAt` | 日時 | 行の更新時刻 |

- 日時は Core.Data の `DbTime` とネイティブ日時型を使う（`M0001` と同じ）。MySQL は表ごと `utf8mb4` へ直す（`M0001` と同じ）。
- **アクセストークンは保存しない**（案）。`run` は単発の実行で、source ごとに 1 回、更新して、メモリだけで使う。
  保存しない利点は、DB のバックアップ・流出から取り出せるものを、暗号化した更新トークンだけに絞れること。代わりに、1 回の実行ごとに更新の呼び出しが 1 回要る。
  寿命が短いトークンを保存する利点は、このツールの実行頻度では小さい。
- トークンの文字列・メールアドレスは、`IngestItems` など、他の表に入れない。

### 6.2 暗号化の方式と鍵

- **方式（推奨）**: AES-256-GCM。12 バイトの乱数のナンス、16 バイトのタグ、**AAD（追加認証データ）に文脈を入れる**。
  書式は、Core の `SecretProtector`（`VehicleVision.PleasanterTools.Core.Web`、
  [v0.1.1 の実装][core-protector]）と同じ `v2$<nonce>$<tag>$<cipher>`（各 Base64）にそろえる。
  この実装は、**明示的に渡された 32 バイトの鍵だけ**を使い、鍵の生成・保存・設定の解決はしない。
- **文脈（AAD）**: `mailingest:oauth2:refresh-token:{sourceId}:{provider}`。行を別の source へ付け替えても復号できない。
  設定の変更（クライアント ID・テナント・メールボックス）は、`GrantFingerprint` で検出する。
- **鍵の渡し方**: 設定の `tokenKeyEnvironmentVariable` が指す環境変数 / `_FILE`（第 4.4 節）。DB の接続設定とは別に渡し、
  **DB のバックアップと同じ場所に鍵を置かない**運用を、文書で求める。
- **鍵を失ったとき・間違えたとき**: 復号に失敗する。`Status` は変えず、`OAuth2KeyMismatch` で source を止める。再認可すれば、新しい鍵で上書きできる。
- **鍵の交換**: 初版は単一の鍵。交換は「新しい鍵を渡して再認可」で代替する。専用のコマンド（`oauth2-rekey`）や、複数の鍵（鍵 ID の列）は後続。
- **置き場の問題**: `SecretProtector` は **Core.Web** にある。Core.Web は ASP.NET Core の共有フレームワーク（`FrameworkReference Microsoft.AspNetCore.App`）に依存する。
  本ツールは「Core.Web は今回は不要」と決めており（[アーキテクチャ方針](アーキテクチャ方針.md) 第 2 節）、CLI に ASP.NET Core の依存を足したくない。
  Core.Data には、暗号化の部品が**無い**（確認済み: Core のリポジトリで `AesGcm` を使うのは Core.Web の `SecretProtector` のみ）。
  選択肢は Q3 で聞く。**推奨は、Core 側で `SecretProtector` を、フレームワーク参照の無いプロジェクト（Core.Data か小さな新規部品）へ置く**こと
  （共有物は Core に置く方針）。

[core-protector]: https://github.com/vehiclevisionjp/VehicleVision.PleasanterTools.Core/blob/v0.1.1/src/VehicleVision.PleasanterTools.Core.Web/SecretProtector.cs

### 6.3 更新の流れ（`run` の中）

source ごとの実行排他（`SourceRuns`）を取った**後**、メールサーバーへ接続して、サーバーの広告から認証方式を計画した**後**に、トークンを得る（無駄な更新と、アクセストークンの滞留を避ける）。

1. 行を読む。無い・`Status` が `Active` でない・`GrantFingerprint` が現在の設定と違う・復号に失敗: 更新せずに止める（第 8 節）。
2. トークンのエンドポイントへ `grant_type=refresh_token` を **1 回だけ**送る。再試行のループを作らない（ロックアウトの回避。パスワード認証と同じ原則）。
3. 応答に新しい更新トークンがあり、値が違うとき: **新しい値を暗号化して、`TokenVersion` の比較つきで書き込む**。
   書き込みに失敗したら、**そのアクセストークンを使わず**、source を止める（`OAuth2StoreFailure`）。古い更新トークンは、Microsoft では取り消されない（2.1）ので、DB の古い行は引き続き有効なはず。
   ただし仕様上は、サーバーが古いものを取り消してよい（2.1）。この危険を残さないため、**書き込みの成功を確かめてからアクセストークンを使う**。
4. アクセストークンを、メモリだけで使い、`SaslMechanismOAuth2(ユーザー, アクセストークン)` で認証する。接続の終了後、参照を捨てる。保存しない。ログ・例外に出さない。

**クラッシュの窓**: 手順 2 の応答を受けた後、手順 3 の書き込みの前にプロセスが落ちると、新しい更新トークンは失われる。
Microsoft は古い更新トークンを取り消さないため（2.1）、DB の古い値で、次回も更新できる見込み。**ただし実機で確かめていない（未確認）**。
Google が回転するかは未確認（2.2）。

### 6.4 同時実行（複数プロセス・実行排他との関係）

- **更新は、source ごとの実行排他の中だけで行う。** 既存の設計（[取り込み状態と API](取り込み状態とAPI.md) 第 3 節）は、単一ホスト・source ごとの排他で、
  同じ source の `run` を同時に走らせない。更新トークンは source ごとに別の行なので、`run` どうしの回転の競合は起きない。
- **`oauth2-authorize` も、同じ排他を取る**（第 5.2 節の手順 2）。認可の最中に `run` が走って古い値で更新する競合を防ぐ。
- 排他とは別に、`TokenVersion` の比較つき更新（楽観的な排他）を**防御として**入れる。更新件数が 0 なら `OAuth2TokenConflict` で止まる（通常は起きない）。
- 同じメールボックスを 2 つの source が使うなら、**認可は source ごとに別**（更新トークンを共有しない）。Google は 1 アカウント・1 クライアント ID あたり 100 個の上限がある（2.2）。
- 異常終了で残った排他は、既存の課題（#85）のとおり、人が確認して解除する。

### 6.5 失敗の分類（更新の失敗・失効）

| 事象 | 分類（[アーキテクチャ方針](アーキテクチャ方針.md) 第 6 節） | `Status` | 停止の理由（サマリーのコード） |
| --- | --- | --- | --- |
| 行が無い | 設定・認証（人の修正待ち） | — | `OAuth2NotAuthorized` |
| `Status` が `ReauthorizationRequired` | 設定・認証 | 変えない | `OAuth2ReauthorizationRequired` |
| `GrantFingerprint` の不一致 | 設定・認証 | 変えない | `OAuth2GrantMismatch` |
| 復号の失敗（鍵の誤り・紛失） | 設定・認証 | 変えない | `OAuth2KeyMismatch` |
| 更新が `invalid_grant`・`consent_required`・`interaction_required` | 設定・認証 | **`ReauthorizationRequired` にする** | `OAuth2ReauthorizationRequired` |
| 更新が `invalid_client`・`unauthorized_client`・`invalid_scope` | 設定・認証 | 変えない（設定の誤りで、トークンは無傷かもしれない） | `OAuth2ClientRejected` |
| 通信失敗・タイムアウト・5xx・`temporarily_unavailable` | 一時的 | 変えない | `OAuth2TokenEndpointUnavailable` |
| 書き込みの失敗・版の不一致 | 設定・認証（運用の確認） | 変えない | `OAuth2StoreFailure` / `OAuth2TokenConflict` |
| メールサーバーが XOAUTH2 を拒否（トークンは取れた） | 設定・認証 | **変えない** | 既存の `MailAuthentication`（原因の候補は第 8 節） |

- 失敗した source だけを止める（他の source へ波及させない。既存の方針）。**メールサーバーへ触れない回復（結果不明の照合・受信後操作の再開）は、従来どおり行う**（#82）。
- 毎回の実行で、失効した更新トークンを使って更新を試みない（`Status` を見て、通信せずに止まる）。
- 終了コード: 上の失敗は、source の停止として、既存の規則どおり **1**（処理エラー）。起動時の検査の失敗（第 7 節）は **2**。

## 7. 起動前検査（`IngestRunner.Check`・`validate`）への影響

既存の `Check` は、設定の対応づけ・タイムゾーン・受信後操作に加え、メール認証情報の変数（ユーザー名・パスワード）を、
接続の前にすべて解決して確かめる（値は返さず、変数名・設定の位置だけを返す）。`validate` は接続せず、秘密の値は読まず、競合と未設定だけを見る。

### 7.1 静的な検査（`validate` と `run` の起動時）

| 検査 | エラー / 警告 |
| --- | --- |
| `mode` と `oauth2` ブロックの整合（`oauth2` に `oauth2` ブロックが無い、`password` に有る） | エラー |
| `password` の項目（パスワード変数・`allowInsecurePasswordAuthentication`）が `oauth2` にある | エラー（混在の禁止） |
| `provider` が未知 | エラー |
| `allowedMechanisms` が `["XOAUTH2"]` でない（省略・空・他の値・重複） | エラー |
| `tls` が `None` | エラー（`OAuth2RequiresTls`。第 9 節） |
| `tenant`・`clientSecretEnvironmentVariable` の有無がプロバイダーの規則に反する | エラー |
| `clientId` が空 | エラー |
| `host` がプロバイダーの想定と違う | 警告 |
| 鍵・ユーザー名・（設定されていれば）クライアントシークレットの変数に、`NAME` と `NAME_FILE` の同時指定 | エラー（`SecretConflict`） |
| 同変数が未設定 | `validate` は警告（`SecretNotSet`）。`run` はエラー（`SecretUnresolved`。解決を試みる） |
| 鍵が解決でき、Base64 で 32 バイトでない（`run` のみ。値は出さない） | エラー（`TokenKeyInvalid`） |
| プロバイダーの実装が、この版に無い（共通の部分だけが入った版） | エラー（`ProviderNotImplemented`） |

パスワードの変数の検査（`userEnvironmentVariable`・`passwordEnvironmentVariable`）は、`oauth2` のときは、**ユーザー名とクライアントシークレットと鍵**の検査に置き換える。

### 7.2 状態 DB を見る検査（`run` の起動時。source ごと）

DB のトークンの有無は、DB に接続できる `run` でしか分からない（`validate` は DB を開かない）。`run` は、接続の前に、`oauth2` の source ごとに次を確かめる。

| 検査 | 失敗 |
| --- | --- |
| 行が有る | `OAuth2NotAuthorized` |
| `Status` が `Active` | `OAuth2ReauthorizationRequired` |
| `GrantFingerprint` が現在の設定と一致 | `OAuth2GrantMismatch` |
| 更新トークンを復号できる（復号した値は捨てる。**値は出さない**） | `OAuth2KeyMismatch` |

**失敗の扱い（推奨）**: 静的な検査の失敗は、既存の規則どおり、起動エラー（終了コード 2・何も実行しない）。
DB を見る検査の失敗は、**その source だけを止め、他の source は実行する**（終了コード 1）。
未認可の source が 1 つあるだけで、他の source の取り込みが全部止まるのを避けるため。Q7。

検査は、**プロバイダーへ通信しない**（トークンの有効性は、`run` の中の更新で初めて分かる）。`oauth2-status` は、7.2 を実行して、結果のコードだけを表示する。
いずれの検査も、秘密の中身（トークン・鍵・ユーザー名）を出力しない。

## 8. 運用サマリー・通知・障害の切り分け

- **サマリー・`--json`・構造化ログには、固定のコードだけを出す**（第 6.5 節のコード）。トークン・鍵・メールアドレス・エンドポイントの応答の本文は出さない。
- 運用サマリーには、source ごとの `StopReason` として上のコードが出る。終了コード 1 になる。
- **管理者への通知は、ツールの機能にしない**（推奨）。ツールは、終了コードとサマリーで失効を知らせる。
  通知は、実行するスケジューラー・監視（終了コードの監視）に任せる。`oauth2-status` を定期的に実行して、`RefreshedAt` が古い（初期候補: 60 日超）ことを検知する運用も勧める
  （Microsoft の既定の寿命 90 日・Google の未使用 6 か月、2.1・2.2）。ツール自身のメール送信は、対象外（SMTP 送信は対象外）。Q8。

メールサーバーが、トークンを取れたのに XOAUTH2 を拒否する場合の原因の候補（実機では未確認の仮説）:

| プロバイダー | 候補 |
| --- | --- |
| Microsoft 365 | そのメールボックスで IMAP / POP3 が無効（2.1）。スコープの不足。アプリケーションの許可を使う場合は、`New-ServicePrincipal` の ID の取り違え（2.1）・メールボックスの許可（`FullAccess`）の不足。共有メールボックスは、ユーザー名の指定が違う（2.1） |
| Gmail | Gmail 側で IMAP が無効、または Workspace の管理者が OAuth のアクセスを制限（`admin_policy_enforced`。2.2）。**サーバーが XOAUTH2 を広告しない（OAUTHBEARER だけ。2.3）** |

## 9. 認証許可リストとの整合・接続保護

- 設定の `allowedMechanisms` は `["XOAUTH2"]` の**明示が必須**で、
  `password` の方式（`PLAIN`・`LOGIN`・`USER/PASS` など）との**混在は禁止**。1 つの source は 1 つの認証方式。
- 認証の前に、**サーバーの広告（`AuthenticationMechanisms`）・MailKit が対応する方式・設定の許可リストの共通部分**を取る（`PasswordAuthenticationPlanner` と同じ考え方の純粋な計画）。
  共通部分が空（サーバーが XOAUTH2 を広告しない）なら、**認証を送らずに停止**（`NoAllowedAuthentication`）。**トークンも更新しない**（6.3 の手順の順序）。
- 認証は `client.AuthenticateAsync(new SaslMechanismOAuth2(ユーザー, アクセストークン))` と、方式を直接渡す（2.3）ので、パスワードや他の SASL へ**フォールバックしない**。
  XOAUTH2 が失敗しても、パスワード認証を試さない。同じトークンを使い回して再試行もしない。
- `IMailAuthenticator` の新しい実装 `OAuth2Authenticator`（`.Mail/Auth`）を、
  `MailKitSessionFactory` の `authenticatorFactory` で、`mode` に応じて選ぶ。
  資格情報の型は、パスワード用の `MailCredentials`（ユーザー名・パスワード）のままでは足りない。
  実装では、**ユーザー名とアクセストークンの提供元（`IAccessTokenProvider`）を持つ別の資格情報**へ抽象化する（案。細部は実装の PR で、`IMailSessionFactory.ConnectAsync` の引数を整理する）。
- **接続保護**: OAuth2 は `tls: Tls` または `StartTls` のときだけ許す。`None` は起動エラー（`OAuth2RequiresTls`）。
  アクセストークンは、パスワードと同じく平文で流してはならない。`StartTls` は、昇格と証明書検証の成功の後にだけ認証する（既存の規則のとおり）。
  `allowInsecurePasswordAuthentication` のような緩めるフラグは設けない。
- 証明書の検証（`certificateValidation`）は、既存の規則（`Strict` が既定）のまま。トークンのエンドポイントへの HTTPS は、OS の信頼ストアで検証し、緩めない。

## 10. セキュリティの検討

### 10.1 漏えい経路と対策

| 経路 | 対策 |
| --- | --- |
| ログ・構造化ログ | 許可する項目は、既存の規則（runId・sourceId・コード・件数など）のまま。トークン・コード・鍵・ユーザー名・URL を出さない。`ProtocolLogger` と HTTP のボディログは有効にしない（既存の規則） |
| 例外のメッセージ | `HttpRequestException` などの文言を、そのまま出さない（トークンのエンドポイントの URL・応答を含み得る）。固定のコードへ置き換えて投げ直す（`SecretResolver` と同じ流儀）。メールの認証失敗は、既存の `MailConnectionException` の固定文 |
| 運用サマリー・`--json` | 固定のコードだけ（第 8 節） |
| コマンドの引数・環境変数の一覧 | トークンもコードも引数で受けない。`--paste` は標準入力から、エコーなしで読む。**秘密を渡す環境変数は、鍵・クライアントシークレット・ユーザー名だけ** |
| DB・バックアップ | 更新トークンは暗号化（AES-256-GCM）。**鍵は DB と別**に渡す。アクセストークンは保存しない |
| 待ち受けの要求 | URL（認可コードを含む）を記録しない。応答の本文は固定 |
| プロセスのメモリ・クラッシュダンプ | 対象外（初版）。トークンは使う間だけ保持する |
| 認可の URL の出力 | `client_id`・`state`・`code_challenge`・スコープだけ。秘密を含まない。ただし、貼り付けるリダイレクトの URL には**コードが含まれる**ので、端末の履歴・画面共有に注意（`--paste` の入力はエコーしない） |

### 10.2 権限の最小化

- スコープは設定にさせず固定（第 4.1 節）。Microsoft は IMAP または POP のスコープだけ（`Mail.Read` や Graph の権限を要求しない）。
- Gmail は、`https://mail.google.com/` が唯一の通常のスコープで、**狭められない**（読み取り専用の IMAP 用スコープは、今回の一次情報に無い）。
  このスコープは、メールの完全な削除までを含む記述（2.2）で、権限が広い。**初版は `leave` のみ（#84 の前）なので、ツールはメールを変更しないが、トークン自体は広い権限を持つ**。保存の暗号化と、鍵の分離が、より重要になる。
- Microsoft のアプリ専用（Q4）は、`FullAccess` を**メールボックスごとに**許可できる（2.1）。委任より、対象を絞りやすい。

### 10.3 ループバックのリダイレクトの安全性

第 5.5 節のとおり。要点は、**ループバックだけに束縛・最初の正しい 1 回だけ受ける・`state` と PKCE で、別のプロセスが偽の応答を差し込む攻撃に耐える・応答にパラメーターを出さない**。
同じホストの別のユーザーが、先に同じポートを取る攻撃は、空きポートを開始時に選ぶ（`--port` 指定時は運用者が管理する）ことと、PKCE（`code_verifier` を持つのは自分だけ）で、コードを使えなくする。

### 10.4 CSRF 相当と PKCE

- `state`: 32 バイトの乱数、1 回の認可の間だけ有効、応答と**定数時間**で比較し、不一致は無視する（OAuth 2.0 の CSRF 対策。Microsoft・Google ともに推奨、2.1・2.2）。
- PKCE: `S256` のみ（`plain` は使わない）。`code_verifier` は 43 文字以上の暗号論的乱数。Google はデスクトップアプリで必須、Microsoft は全種類で推奨（2.1・2.2）。
- `nonce`・`id_token` は要求しない（`openid` のスコープを使わない）ので、ID トークンの検証は無い。
  認可したアカウントとメールボックスが一致するかの確認は、第 5.2 節の手順 9（認証の試験）に任せる。

### 10.5 トークンのエンドポイントの呼び出し

- HTTPS のみ。リダイレクトを辿らない・Cookie 無し（Pleasanter 用の `HttpClient` と同じ流儀）。応答の大きさに上限を置く（初期候補: 64 KiB）。タイムアウトは 30 秒（既存の初期候補）。
- エンドポイントはコードに固定。`tenant` は形式を検査してから URL に使う（パスの注入を防ぐ）。
- 依存ライブラリ（MSAL・Google.Apis.Auth）を使うかは Q2。**推奨は使わず、薄い自前の実装**にして、トークンの保存・回転を自分で制御する（決定 8-1）こと。
  MSAL は独自のキャッシュに更新トークンを持つ。DB へ暗号化して保存する決定とは、キャッシュの直列化の差し込みが要り、扱う面が増える（未検証の推測）。

## 11. ユーザーへの質問（順に 1 つずつ答える）

選択肢と推奨を示す。決まるたびに、この文書の該当の節を直す。

### Q1. 認可の方式と、画面の無いサーバーの手順

- A（推奨）: 認可コード + PKCE + ループバックだけ。画面の無いサーバーには、`--port` + SSH のポート転送と、`--paste` の両方を初版で用意する。Microsoft のデバイスコードは入れない。
  理由は第 3.2 節（Gmail が使えない・方式を 2 本にしない）。
- B: A に、Microsoft 365 だけデバイスコード（`--device-code`）を足す。画面の無いサーバーでは楽だが、方式が 2 本になり、Gmail と手順が違う。
- C: `--paste` を初版に入れず、SSH のポート転送だけにする。実装は減るが、SSH を使えない環境で詰む。

### Q2. トークンの取得の実装

- A（推奨）: 薄い自前の実装（標準の `HttpClient`。PKCE・認可 URL・コードの引き換え・更新・エラーの分類）。依存の追加なし、更新トークンの保存・回転を自分で制御できる。セキュリティ上の細部は、自前の試験で担保する。
- B: MSAL.NET（Microsoft）と Google.Apis.Auth（Google）を使う。実績があるが、依存が 2 つ増え（ライセンス・NOTICE・脆弱性の追従）、独自のキャッシュと、決定 8-1（DB へ暗号化して保存）の整合に作り込みが要る。

### Q3. 暗号化の部品の置き場

- A（推奨）: Core 側で、`SecretProtector`（AES-256-GCM）を、ASP.NET Core に依存しない場所（Core.Data か、小さな新規部品）へ置く（別リポジトリの作業。本 PR では触れない）。
  本ツールは、それを使う。実装の PR は、その公開の後。
- B: 本リポジトリの `.Data` に、同じ書式（`v2$...`）の小さな実装を持つ。早いが、複製になる（後で Core へ寄せる課題が残る）。
- C: Core.Web を参照する。ASP.NET Core の共有フレームワークへの依存が CLI に入るため、不採用を推奨。

### Q4. Microsoft 365 のアプリ専用（クライアント資格情報）

- A（推奨）: 初版は委任（ユーザー）だけ。`oauth2.grant` に拡張点を残し、アプリ専用は別の Issue で後続にする（決定 8-1 の更新トークン保存を前提にしているため）。
- B: 初版からアプリ専用も作る。無人運用に向く（更新トークンも、パスワード変更による失効も無い見込み）。ただし、PowerShell でのサービスプリンシパルの登録・メールボックスごとの許可の手順と、未確認事項（2.1）が増え、実機の検証が重くなる。

### Q5. Gmail の認証方式の名前

- A（推奨）: `XOAUTH2` だけ（決定 8-3 のとおり）。Gmail が XOAUTH2 を広告しないことが実機で分かったら、そのときに追加を決める。
- B: 許可値に `OAUTHBEARER` も足す（`allowedMechanisms` は `["XOAUTH2"]` か `["OAUTHBEARER"]` のどちらか 1 つ。混在させない）。
  MailKit の Gmail の文書は OAUTHBEARER を使うため。ただし、これは決定 8-3 の拡張になる。

### Q6. Gmail のサービスアカウント（ドメイン全体の委任）

- A（推奨）: 初版の対象外。範囲外として、第 1 節に明記する（取得手順を一次情報で確認していない）。
- B: 初版から含める。Workspace の無人運用に向くが、取得手順の確認と、実機の検証が増える。

### Q7. 未認可の source の扱い（起動前検査）

- A（推奨）: DB を見る検査（行が無い・失効・設定の不一致・鍵の誤り）の失敗は、**その source だけを止め**、他の source は実行する。終了コード 1。
- B: 起動エラー（終了コード 2）にして、**何も実行しない**。既存の「起動前検査の失敗は全体を止める」に合う。ただし、1 つの source の失効で、全部が止まる。

### Q8. 管理者への通知

- A（推奨）: ツールは通知しない。終了コード・サマリーのコード・`oauth2-status` で知らせ、通知は監視に任せる。
- B: 失効の検知時に、設定した宛先へメールを送る。SMTP 送信が対象外であり、秘密（SMTP の資格情報）が増えるため、避けたい。

### Q9. アプリの登録・OAuth クライアントの用意

- A（推奨）: 運用者が自分のテナント・プロジェクトで登録する。配布者は、登録の手順を文書で示すだけ（共通のクライアント ID を配布しない）。Google の制限付きスコープの審査の負担が、配布者に生じない。
- B: 配布者が共通のアプリを用意する。運用者は楽だが、Google の制限付きスコープのセキュリティ評価（12 か月ごと。2.2）と、Microsoft のマルチテナントの公開の責任が、配布者に生じる。

## 12. 実装の分け方（案）

実装用の子 Issue は、この設計のレビュー後に起こす（今は起こさない）。

| 順 | PR（案） | 内容 | 前提 |
| --- | --- | --- | --- |
| 0 | （Core）暗号化の部品 | Q3 の A のとき。本リポジトリの作業ではない | — |
| 1 | 共通の部分 | 設定の解析と静的な検査（第 4・7.1 節）。`M0002` と `OAuthTokenStore`（楽観的な排他）。暗号化の利用。トークンの更新とエラーの分類。`OAuth2Authenticator`・計画・資格情報の抽象化。`oauth2-authorize` の本体（待ち受け・PKCE・`state`・`--paste`）と `oauth2-status`。`run` への統合（DB を見る検査・停止の理由・サマリー）。**プロバイダーは差し込みの形にし、実装は無い**（`ProviderNotImplemented`）。偽の認可サーバーで試験 | 0 |
| 2 | Microsoft 365 | プロバイダーの実装（エンドポイント・スコープ・エラーの対応）、`tenant` の検査、設定例・README・参照資料の更新、実機の確認の手順書 | 1 |
| 3 | Gmail | 同上（Google の認可のエンドポイントと、`access_type=offline` などの条件を、原文で確認してから） | 1。2 と並行できる |

### 試験の方針

- **単体**: PKCE（RFC 7636 の付録のテストベクター）、`state` の定数時間比較、認可 URL の組み立て、スコープの固定、設定の検査（第 7.1 節の全項目）、エラーの分類（第 6.5 節の表）、
  暗号化の往復と AAD の不一致・鍵の誤りでの失敗、**漏えいの試験**（トークン・コード・鍵に見立てた目印の文字列が、例外・ログ・サマリー・`--json` に出ないこと）。
- **統合（偽のサーバー）**: ループバックの偽の認可サーバー兼トークンのエンドポイント（`HttpListener`）で、認可 → 保存 → 更新（回転・書き込みの失敗・版の不一致）→ 失効（`invalid_grant`）を通す。
  偽の IMAP / POP3 で、`AUTHENTICATE XOAUTH2` の文字列の形（2.1・2.2 の形式）と、広告に XOAUTH2 が無いとき**認証もトークンの更新も送らない**ことを試す。
  GreenMail が XOAUTH2 に対応するかは未確認なので、自前の偽サーバーを使う前提にする。
- **実機（ユーザーの環境）**: Microsoft 365 のテスト用のテナント・メールボックスと、Gmail のテスト用アカウントが要る。ユーザーが用意する（決定 8-2）。

### 未検証になる範囲（実機が無いと確認できないもの）

- Microsoft: アプリの登録の手順、`http://localhost` のリダイレクトと IPv6 の挙動、更新トークンの回転の実際（古い値が引き続き使えるか）、90 日の失効、パスワード変更による失効、IMAP 無効時の挙動、アクセストークンの寿命。
- Gmail: XOAUTH2 の広告の有無、認可のエンドポイントと `access_type=offline` の要否、クライアントシークレットの要否、Testing の 7 日の失効、In production・未審査のときの更新トークンの寿命、更新トークンが回転するか。
- 共通: 長時間の実行の途中でアクセストークンが切れたときのサーバーの挙動（接続の確立時に検証されるのが一般的だが、今回の資料では**未確認**）。

## 13. 実機の未検証と、確認していない仕様について

- **この文書は設計であり、実装も実機の検証もない。** 第 2 節は、公開文書の記述を確認したもので、接続して動作を確かめたものではない。
- Google の各ページは要約を介した確認で、更新日の記載がなかった。数値（7 日・6 か月・100 個・12 か月）は、実装時に原文で再確認する。
- 「未確認」と書いた項目（Microsoft のアクセストークンの既定の寿命・クライアント資格情報の更新トークン・ホスト名とポート・IMAP の PowerShell のパラメーター名、
  Google の認可のエンドポイント・`access_type=offline`・更新トークンの回転・アクセストークンの寿命・DWD のトークン取得）は、事実として扱わない。
- 一次情報が将来変わり得る。実装の PR で、参照日を更新して再確認する。
