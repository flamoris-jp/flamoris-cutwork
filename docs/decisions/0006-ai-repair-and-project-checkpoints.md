# ADR 0006: AI repair workflow and revisioned project saving

Status: **Proposed / 設計提案・未実装** — 2026-10-24.

This document proposes extensions to the current production baseline. It does not
enable new tools or supersede current runtime permissions until implementation,
review, and acceptance are complete. Current contracts remain in
[Live MCP](../live-mcp.md), [Clone](../phase5-clone-repair.md),
[finishing](../phase6-repair-finishing.md), and
[persistence](../phase7-persistence-export.md).

## 1. Problem and product boundary

実画像のパーツ分離・下地補修で、口は良好だったが、目と眉には色の境界、
過剰な赤み、髪の切れ目が残った。観測できるだけでは品質は保証されない。
採取元・色・毛流れを判断し、局所的に試して比較する操作が必要だった。
[当日の実作業メモ](../ai-guide/2026-10-24-portrait-repair.md)を参照。

人間の通常UIは既存のPart / Mask / Clone / Patch / Blur / Smudgeを中心とする。
AIの補助線、採取元候補、比較用データを常設のツールバーに追加しない。
共通の描画機能は同じCore/Imagingカーネルを使い、AI向けの読み取り・試行・
採用はMCPアダプターで提供する。別の文書モデルやAI専用描画エンジンは作らない。

## 2. Current baseline / proposed delta

| 項目 | 現在 | 提案 |
|---|---|---|
| Cloneの採取 | immutable Original、Fixed / Offset、等倍 | 元画像の倍率・回転をブラシ半径と独立させる |
| Cloneの縁 | 硬い円、上書き | hardness / opacityを指定する |
| 補修済み色の再利用 | Cloneでは不可 | ストローク開始時の合成画像を凍結して採取 |
| 試し塗り | Part候補のみ既存 | Clone候補を非破壊プレビューし、同じ結果を採用 |
| 仕上げ | UIのRepairにBlur / Smudge、MCP未公開 | 同じ対象条件のままMCPに公開 |
| AI保存 | 不可、UI保存のみ | 指定済みプロジェクトへの版付き保存 |

原画は引き続き不変。既存のpayloadと既存作品の見え方を変えない。

## 3. Clone mapping and brush contract

提案する追加値（初期上限。性能測定後に受入で確定する）：

- `sourceKind`: `Original`（既定）または`CompositeAtStrokeStart`。
- `sourceScale`: 0.25–8、既定1。2なら採取画像を2倍に拡大して描く。
- `sourceRotationDegrees`: -180–180、既定0。画像座標で時計回りを正とする。
- `hardness`: 0–1、既定1。`opacity`: 0–1、既定1。
- `radius`, `points`, Fixed / Offset, target / ownerPart / globalの明示条件は維持。

採取元の大きさは独立した第三の半径で指定せず、`radius / sourceScale`で導く。
例えば描画直径40px、倍率4なら、採取直径は10pxとなる。ブラシを太くしても、
倍率を調整して目や唇を採取範囲から避けられる。

画素中心p、現在dab中心d、開始点d0、元画像アンカーa、倍率s、回転行列Rとする。
候補・採用・UIのすべてで次の写像を共有する：

```text
Fixed:  source(p, d) = a + inverse(R) * (p - d) / s
Offset: source(p, d) = a + inverse(R) * (p - d0) / s
```

Offsetは各dabの中央をリセットしないので、連続した元の毛束をたどれる。
斜めにストロークしてもテクスチャを自動で接線方向へ曲げない。
曲線へのワープや接線追従回転は本提案の初期実装に含めず、必要性を別途検証する。

変換後はpremultiplied alphaでbilinear採取し、出力は既存のstraight BGRAへ戻す。
範囲外や透明な元画素は既存描画を消さず、寄与なしとする。
`hardness=h`では中心からh×radiusまで重み1、外周までsmoothstepで0に落とす。
opacityと掛けてsource-over合成する。1ストローク内は各画素の最大被覆量を使い、
点の密度やdab分割でopacityが累積して濃くなることを避ける。
次のストロークでは通常どおり重ね塗りできる。

互換分岐：Original / scale=1 / rotation=0 / hardness=1 / opacity=1は現行の
floor採取・last-covering-dab上書きとbyte単位で一致させる。新規方式を黙って
既存ストロークへ適用しない。Undo/Redoは計算の再実行ではなく確定画素を記録する。

## 4. Frozen Composite sampling

`CompositeAtStrokeStart`はそのストローク直前の合成画素を採る。
AIは目のPartを隠して下地を参照するか、意図する表示状態を明示してから採取する。
表示中の目を自動で除外したり、透明な穴を肌と推定したりはしない。

同じストローク中に描いた画素は採取しない。次のストローク開始時には、
直前の補修を含む新しい合成画像を採れる。これにより周囲から内側へ段階的に進められる。
まず全パスを検証して採取範囲の和集合とbilinear haloを求め、必要なROIだけを凍結する。
文書全体をdabごとにコピーしない。原画・合成・表示状態のrevisionを候補に束縛する。
複数操作のbatchでは操作順に採取し、各stroke開始時点を明確にする。

新規保存項目はまず不要。Repairは既存v2の確定画素、Patchは既存の凍結画素と
transformを保存する。候補・一時参照画像・補助線・保存先情報は`.flimg`に入れない。

## 5. AI observation and trial loop

以下は**提案名**であり現在の呼出し可能ツールではない。

| 提案操作 | 入出力と効果 |
|---|---|
| `repair.inspect` | 小さなROI、明示Part/Mask。hole mask、輪郭座標、周囲の色分布・alpha、同一座標の画像を返す。推定線の続きや接線は信頼度付き参考値とする |
| `clone.candidates` | 同一ROIに最大3案。描画前後画像・差分・変更画素数・実際の採取範囲・予算見積を返す。文書/選択/dirty/historyを変更しない |
| `repair.commitCandidate` | candidateIdとguardを受け、見た確定画素を1回の共有Undoとして採用。再描画して別結果にしない |
| `blur.stroke` / `smudge.stroke` | 現在のRepair-only条件を維持した有界操作。UIと同じカーネル、履歴、キャンセルを使う |

候補は最大3、TTL 5分、grant/document/revision/表示状態/targetに束縛。
編集・Undo・Open・権限変更で失効し、古いrevisionへ戻しても復活しない。
同じ候補の2度目の採用を拒否する。切断で結果不明ならcontext/historyを確認し、
編集を自動再送しない。観測だけの権限ではpreviewまで、採用にはEditが必要。

採取元の自動探索は必須にしない。AIが目・眉・唇の混入を画像で確かめ、
肌の色差・毛束の向きが合う元領域を選ぶ。色差の数値は品質判定の代替にしない。

AIの曲線・円などの作業ガイドは文書座標付きの一時的な計画データとして保持する。
通常UIに専用ガイドパネルを増やさない。人間が注釈画像を渡した場合は位置合わせして
参照し、注釈色を採取元や出力へ混入させない。画像だけでは文書との対応が確定しない
ときは推測座標で適用しない。

既存のBlur/SmudgeはPatchを編集できず、合成境界を跨いだ万能ぼかしでもない。
必要なら明示的な`patch.bakeToRepair`を追加する：元Patchと同じ合成結果を持つ
Repairへ1Undoで変換し、元Patchとその位置・IDをUndo可能に保持する。
変換後はPatch transformを使わない。Base/Originalや別のPartへ暗黙に塗らない。

## 6. Revisioned saves: identity and authority

ファイル保存は現在のMCP禁止領域への**限定的な新規契約**として実装・レビューする。
任意パス、フォルダー閲覧、シェル、Open/restoreをまとめて許可しない。
Cutworkが既に選択したプロジェクトに対するSaveをUIで一度許可し、
`project.context`はopaque `saveTargetId`と現在のsave可否だけを返す。
初回未保存作品はUIで保存先を指定してから利用する。Save Asや保存先変更で古いIDを失効。
既存のEdit grantだけで過去の接続にファイル権限を付与しない。
認可した保存先のidentityをホストが保持し、symlink/reparse pointの付替えや保存先の
置換を検出したら拒否する。AIからパス断片を受けず、ホスト生成名だけを使う。

3つの番号を区別する：

1. `documentRevision`：現在の単調増加値。競合防止用。
2. `historyStateRevision`：Undoで戻る状態識別子。保存時の内容とdirty判定用。
3. `checkpointId`：プロジェクトごとの永続的な版ID。再起動・Undoで巻き戻らない。

提案API：`project.checkpoint` / `project.save` /
`project.saveStatus(requestId)` / `project.checkpoints.list(cursor,count)`。
全て現プロジェクトと認可先に限定し、一覧は最大20件。save/checkpointには
通常guardに加え、`saveTargetId`, `expectedCheckpointId`（初回null）, UUIDの
`requestId`, 最大128文字のlabelを指定する。labelはファイル名にしない。
既存版は`expectedWorkingFileHash`でも照合し、UI外の編集を上書きしない。

Cutwork自身が認可された保存先に版ストアを割り当てる。
例示名`r000042.flimg`のように、各版は独立して再Openできる完全なv2アーカイブとし、
差分チェーンやUndo履歴を復旧の前提にしない。番号の欠番は許容するが再利用しない。
sidecar索引にはcheckpointId、親版、UTC日時、label、要求ID、内容digest、
保存時の両revisionとスキーマ版を記録する。認証情報・絶対パス・画像をログへ出さない。

## 7. Save commit and recovery

1. WPFの既存ファイル調整境界を使い、guard・対象ID・親版・外部変更を確認する。
   pending stroke/fence/metadata/file処理中はbusy。黙って確定・破棄しない。
2. 保存対象の不変snapshotと履歴状態を採取する。初期実装では完了まで編集/Open/
   Save Asを排他する。UIとMCPのSaveを同じ排他とサービスへ通す。
3. 既存codec/storeの方針どおり同一保存先内のtempへ書き、close、再読込検証、
   digest検査を完了してから版ファイルをcreate-newで確定する。既存版を置換しない。
4. 版のreceiptを含む索引をtemp→atomic置換で公開する。ここをcheckpoint commit点とする。
   公開前の障害は失敗。公開後の応答消失はrequestIdで結果を照会できる。
5. `project.checkpoint`はここで完了。通常の作業ファイル・ProjectPath・dirtyを変えない。
   `project.save`は続けて現在の認可済み作業ファイルを既存のatomic replaceで更新する。
   再度外部変更を照合し、保存対象の履歴状態に一致するときだけMarkSavedする。
6. 作業ファイル更新だけが失敗した場合は`checkpointSaved=true`,
   `workingCopySaved=false`を返す。版は保持し、dirtyを消さない。
   保存成功を偽らず、同じrequestIdのstatusから残りの処理を明示的に再開できる設計にする。

同じrequestId・同じ内容は既存receiptを返し、新しい版を増やさない。
同じIDで異なる内容は拒否。再起動後も索引から重複を判断できる。
索引公開前のorphan版は有効な旧版へ影響させず、次回検査で識別する。
クラッシュ後は完全な旧版または新版だけを扱い、破損版をHEADへ採用しない。
保存要求のキャンセル/接続解除はcommit前のみ中止し、commit後はreceiptを残す。
複数のCutwork instanceが同じ保存先を使う場合も、版採番と索引更新を保存先ごとの
排他ロックと親版照合で直列化する。外部更新の照合と作業ファイル置換は同じ排他境界に
置き、対象のidentity/hashを保持できない保存先では競合として拒否する。
対応ファイルシステムのatomic性・flush特性を明示して試験し、未検証の共有先へ
ローカルと同じ耐障害性を約束しない。

版保存はUndo項目を増やさず、Undoしても保存版は消さない。復元はまず人間が版を
Openする既存操作を使う。AIによる復元・版削除は初期実装に含めない。
容量不足では古い版を自動削除せず、保存失敗と必要容量を通知する。
版数/総容量の上限はホスト設定で明示し、一覧/statusでも残容量可否を読めるようにする。

## 8. Implementation sequence and acceptance

1. 共通Clone mapping・倍率/回転・hardness/opacity。既存結果の互換テスト。
2. 有界Composite snapshot採取。UIとMCPで同じ結果・疎密点列/分割で同じ結果。
3. inspect / candidates / commit、既存Blur/SmudgeのMCP公開とPatch bake。
4. 認可済み保存先・版ストア・receipt・Saveの共通化。Hubカタログも実装時に更新。
5. 下記の実画像QAで判定してから利用ガイドの「現在使える」欄を更新する。

必要な回帰・障害試験：倍率4ならsource半径が1/4、回転の符号/画素中心、alphaと
画像端、硬さ0/1、opacity0/no-op、同一strokeの再帰採取なし、次strokeで前の補修を
採れること、候補と採用の画素一致、失効/取り違え/二重採用、cancel/Undo/Redo、
候補/採取ROI/halo/変換/growth/dirty合計の資源予算、同時UI編集と保存の競合。
保存はdisk full、書込/検証/rename/索引/作業ファイル各段階の障害、応答消失、
再起動後の重複要求、外部ファイル更新、権限失効、Save As、旧版Openを検証する。

現行のMCP予算を黙って緩めない。拡大率と回転後の採取ROI、Compositeのlayer訪問数、
凍結画像、候補3案と履歴の合計を事前算定する。大きい依頼は明確なresource errorと
縮小すべきROIを返す。保存は別の有界job/status契約とし、進捗・期限・キャンセルを設ける。
開始要求はjobIdを返し、statusは保存中のbusyでも読める。各非同期段階から既存の
Core/文書ゲートへ戻るときに対象・grantを再検査する。commit前の失効は中止し、
commit後の応答はreceiptに従う。保存jobに紐づく文書の排他を終了/失敗時に必ず解放する。

実画像QAは、非表示時の取り残し、通常表示での復元、まばたき/口閉じ/小移動の
実際の書き出しを分ける。肌境界、頬の丸み、髪の連続性を拡大と最終再生サイズで
確認する。目と眉の今回の結果は未合格。数値やテスト合格だけで美的完成としない。
