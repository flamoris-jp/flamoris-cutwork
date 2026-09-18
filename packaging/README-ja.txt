FLAMORIS Cutwork v0.1 (Windows x64 portable)
================================================

1. ZIPを任意のフォルダーへすべて展開してください。
2. 展開先の Cutwork.exe を起動してください。

.NET SDK / .NET Runtime / Python の追加インストールは不要です。
PNG / JPEGを開き、.flimgプロジェクトの保存・再読込、Composite PNGと
Layer Handoffの書き出しができます。

このv0.1配布物はコード署名されていません。Windowsが確認画面を表示した場合は、
信頼できるFLAMORIS配布元から取得したZIPであることを確認してください。

ローカルMCP:
画像を開いた後、MCP / AI メニューで「読み取り専用」または「編集を許可」を
有効にし、表示された設定JSONを同じPC・WindowsユーザーのMCPクライアントへ
コピーしてください。同梱の mcp/Cutwork.Bridge.exe を使用します。
初期状態はOffです。停止・権限変更・文書の再Openで古い接続は失効します。
画像へのアクセスを許可しますが、MCPからのOpen/Save/ファイル書き出しはできません。
クラウド側のクライアントへの中継機能はありません。
手順と対応範囲: https://github.com/flamoris-jp/flamoris-cutwork/blob/main/docs/live-mcp.md

ライセンス:
- FLAMORIS Cutwork: LICENSE.txt (Mozilla Public License 2.0)
- MCP C# SDK: MCP-SDK-LICENSE.txt (Apache License 2.0)
- 同梱.NET Runtime/WPF: DOTNET-LICENSE.txt / THIRD-PARTY-NOTICES.txt

ソースコード:
https://github.com/flamoris-jp/flamoris-cutwork
