# Phase 8: Windows packaging

## 配布対象

Cutwork v0.1の最小配布物は、Windows x64向けportable ZIPです。clean-machine
acceptanceはWindows 11 x64で行います。インストーラーや管理者権限は不要です。

ユーザー操作は次の3手順です。

1. GitHub Actionsの`FLAMORIS-Cutwork-v0.1.0-win-x64` artifactを取得する。
2. `FLAMORIS-Cutwork-v0.1.0-win-x64.zip`を任意のフォルダーへすべて展開する。
3. 展開先の`Cutwork.exe`を起動する。

配布物には.NET 10 Desktop Runtimeを含むため、対象PCに.NET SDK/runtimeは不要です。
PythonおよびPython版experimentも不要です。

このv0.1 binaryはコード署名していません。取得経路やPCのポリシーによって
Microsoft Defender SmartScreenが確認を表示する場合があります。信頼できる
FLAMORISの配布元から取得したartifactであることと、同梱inventoryのSHA-256を
確認してください。publisher名や署名済みであるかのようなmetadataは設定して
いません。

## 作成方法

repository rootで次を実行します。

```powershell
pwsh -File eng/package-windows.ps1
```

scriptが実行するproduction publishの要点は次のとおりです。

```powershell
dotnet publish src/Cutwork.App/Cutwork.App.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  /p:PublishProfile=win-x64
```

profileは`src/Cutwork.App/Properties/PublishProfiles/win-x64.pubxml`です。
WPFの互換性と診断可能性を優先し、single-file、trimming、ReadyToRunは使用しません。
入口となるproduct executableは`Cutwork.exe`だけです。

出力:

- `artifacts/publish/win-x64/`: 検査前のself-contained publish出力
- `artifacts/package/FLAMORIS-Cutwork-v0.1.0-win-x64/`: ZIP staging
- `artifacts/FLAMORIS-Cutwork-v0.1.0-win-x64.zip`: portable配布物
- `artifacts/FLAMORIS-Cutwork-v0.1.0-win-x64.inventory.json`: path、size、SHA-256

## Artifact contract

ZIP rootには次を含みます。

- `Cutwork.exe`とself-contained .NET/WPF runtime files
- `README-ja.txt`
- `LICENSE.txt`（Cutwork、MPL-2.0）
- `DOTNET-LICENSE.txt`
- `THIRD-PARTY-NOTICES.txt`

packaging scriptは、必須app/runtime filesとWPF native runtime、`Cutwork.exe`が唯一の
Cutwork product launcherであること、ZIPの可読性、required noticesを検査します。
source、tests、experiments、
build intermediates、PDB、Python/Tkinter/OpenCV由来ファイルが混入した場合は失敗します。
参照projectからSDKがpublishしたPDBは、専用publish directory内からpackage staging前に
除去し、続くforbidden-content検査で残存がないことを確認します。

Production projectの外部runtime dependencyはありません。OpenCVやPython packageは
production projectから参照されていません。配布物中のnative filesはself-contained
.NET/WPF runtimeに由来し、そのlicense/noticesはpublishに使用したSDKからコピーします。

## 使用できる機能

portable buildでもdevelopment buildと同じauthoritative editorを実行します。

- PNG / JPEGを開く
- Part、Mask、Patch、Clone、Blur、Smudgeによる編集
- `.flimg` Save / Open
- Composite PNG export
- Layer Handoff export

exportはviewport captureではなく既存compositor/persistence boundaryを使用します。
packagingによるschema v1やeditor behaviorの変更はありません。

## MSIXと`.flimg`関連付け

v0.1ではdeferします。現時点のacceptanceはdownload、extract、`Cutwork.exe`起動で
満たせ、Start menu統合、install/uninstall、auto-update、file associationの具体的な
要求はありません。署名・配布経路を決めずにMSIXを追加してもrelease surfaceだけが
増えるためです。`.flimg`はアプリ内のFile > Open Projectから開きます。

## Clean-machine checklist

.NET SDK/runtimeおよびPythonを入れていないWindows 11 x64で確認します。interactive
desktopで実施していない項目をCI結果だけで「確認済み」と扱いません。

- [ ] CI artifactを取得する。
- [ ] ZIPを新しい空フォルダーへすべて展開する。
- [ ] `Cutwork.exe`を起動する。
- [ ] 日本語がdefaultで表示される。
- [ ] Englishへ切り替え、visible stringsが切り替わる。
- [ ] PNGとJPEGを各1枚開く。
- [ ] 小さなPartまたはrepair editを行い、Undo/Redoする。
- [ ] `.flimg`としてSaveする。
- [ ] Cutworkを終了し、再起動する。
- [ ] 保存した`.flimg`をOpenし、layer/order/pixelsが復元される。
- [ ] Composite PNGをexportし、document sizeの画像であることを確認する。
- [ ] Layer Handoffをexportし、ZIP/JSON/PNGを開けることを確認する。
- [ ] Cutworkを終了する。
- [ ] missing runtime/native DLL errorが発生していないことを確認する。
- [ ] `zoom → pan → zoom → pan`後も既存canvas navigationが正常である。

## CI boundary

既存のWindows `Production` workflowだけを使用します。Release restore/build/testの後、
同じpackaging scriptを実行してartifact inventoryとZIPを検査し、ZIPとinventoryを
workflow artifactとしてuploadします。GitHub Releaseの自動作成は行いません。
