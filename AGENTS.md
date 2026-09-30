# AGENTS.md

このリポジトリで作業するエージェント（Claude Code等）向けの背景情報・設計判断・注意点をまとめたものです。
コードやREADMEから自明でない「なぜそうしたか」を中心に記載しています。

## 命名について

このプロジェクトは元々「Jev」という設計思想（テキストを逐次生成せず、フォワードパス直後のLogitsから
特定選択肢の確率を直接取得する）に着想を得て開発され、プロジェクト名やクラス名にも「Jev」を使っていた。
しかし「Jev」は他社の製品名と衝突するため、公開にあたって全面的に「WinJevlyn」へリネームした
（`JevInferenceApp` → `WinJevlyn`, `JevMultimodalApp` → `WinJevlyn.Multimodal`,
`JevMultimodalPlayground` → `WinJevlyn.MultimodalPlayground`, `JevInferenceEngine` → `InferenceEngine`,
`JevMultimodalEngine` → `MultimodalEngine`）。本ファイルの過去の設計判断の記述も新名称に統一している。

## プロジェクトの目的

テキストを逐次生成せず、フォワードパス直後のLogitsから特定選択肢の確率を直接取得するという設計思想に
基づいた、Windows 11向け軽量ローカル推論GUI。GGUF形式の軽量モデル（Qwen2.5系など）をCPU単体で動かし、
文脈と選択肢A/B/Cのうちどれが最も適切かを1回のフォワードパスで判定する。

詳細な動作原理・ビルド手順は [README.md](README.md) を参照。

## リポジトリ構成

```
WinJevlyn.sln
src/
  WinJevlyn/                        本体（WPF GUI + 単一フォワードパスのロジット抽出）
    Services/InferenceEngine.cs     コアロジック（このリポジトリで最も重要なファイル）
    MainWindow.xaml(.cs)            GUI
    Models/ChoiceResult.cs
  WinJevlyn.MultimodalPlayground/   マルチモーダル（画像入力）検証用の独立したコンソールアプリ
  WinJevlyn.Multimodal/             マルチモーダル版GUI本体（配布対象、GPU優先・CPU自動フォールバック）
    Services/MultimodalEngine.cs    コアロジック
    MainWindow.xaml(.cs)            GUI
models/                             GGUFモデルの置き場（.gitignoreで除外、リポジトリには含めない）
```

## 開発環境に関する経緯

- このマシンには元々 .NET 5 系のSDKしか入っておらず、.NET 8 SDKを `winget install Microsoft.DotNet.SDK.8`
  でインストール済み（本体はWPFのため `net8.0-windows` が必須）。
- LLamaSharpのAPIは頻繁に変わるため、実装前に必ず該当バージョン（現在 `0.27.0` 固定）のソースを
  GitHub上で直接確認してから使うこと（`curl` で `raw.githubusercontent.com/SciSharp/LLamaSharp/v0.27.0/...`
  を取得するのが最も確実）。ドキュメントやブログの情報は古いAPIを指していることが多い。

## InferenceEngine の設計判断

[Services/InferenceEngine.cs](src/WinJevlyn/Services/InferenceEngine.cs) の実装で、コードコメントには
書いていない意図：

- **1回のdecodeのみ**: `LLamaBatch.AddRange(tokens, 0, LLamaSeqId.Zero, logitsLast: true)` で
  最後のトークン位置だけlogitsを要求し、`context.Decode(batch)` を1回呼ぶだけ。テキスト生成ループは行わない。
  これがこのアプリの核心部分。
- **候補トークンの特定は差分トークナイズ方式**: `prompt` と `prompt + "A"` をそれぞれトークナイズし、
  共通接頭辞（LCP）以降の最初のトークンを「Aに対応するトークン」とみなす。BPE系トークナイザは前後の文脈で
  同じ文字でも別トークンになり得るため、決め打ちで「"A"のトークンID」を求めるのではなく、実際の文脈で
  差分を取る方式にしている（lm-evaluation-harnessのloglikelihood計算と同じ考え方）。
- **chat templateはtry/catchでフォールバック**: `LLamaTemplate(weights, strict: true)` でGGUF埋め込みの
  chat templateを使おうとし、失敗したら（テンプレート未埋め込みのGGUFなど）プレーンテキストプロンプトに
  自動フォールバックする。どのGGUFを読み込んでも動くようにするため。
- **KVキャッシュは毎回クリア**: `context.NativeHandle.MemoryClear(true)` を推論のたびに呼び、前回の
  会話状態を引きずらないようにしている（このアプリは常に単発の3択判定なので、会話継続は不要）。
- 確率はA/B/Cの3つのlogitだけをsoftmaxしたもの（vocab全体のsoftmaxではない）。「3択のうちどれが
  相対的に尤もらしいか」を見る設計。

## 動作検証の方法（GUIを操作せずコアロジックを検証する手法）

このセッションではWPFのGUIをクリック操作するツールがなかったため、以下の方法で実際の推論を検証した：

1. スクラッチパッド配下に `WinJevlyn.csproj` を `ProjectReference` する軽量コンソールアプリ
   （`EngineSmokeTest`）を作り、`InferenceEngine` を直接呼び出して実モデルで推論させ、標準出力で
   結果を確認した。GUIを介さずコアロジックだけを検証したい場合に有効な手法。
2. GUI自体は `Start-Process` で起動し、数秒後もプロセスが生きているか（=起動時クラッシュしていないか）
   だけ確認した。ボタン操作などの詳細なUI検証は未実施（ツール制約のため）。
3. 実際にQwen2.5-0.5B-Instruct-Q4_K_Mで検証した結果、「フランスの首都は？」→パリを93%で正解した一方、
   「空の色」「2+2」は誤答した。これは実装のバグではなく0.5Bモデル自体の能力限界（単一フォワードパスで
   思考過程を持たない）。より正確な判定が必要な場合はモデルサイズを上げる。

## GGUFダウンロード時の注意

Hugging Faceの `resolve/main/...gguf` URL（xet-bridge経由のリダイレクト）は、単発の `curl -L` だと
接続が途中で切れて **エラーを出さずに途中サイズで終わる** ことがあった（実際に2回発生）。対策として、
`curl -L -C - --http1.1` を使い、期待サイズに達するまでループで再試行する方式が有効：

```bash
EXPECTED=<HEADのcontent-lengthで確認したバイト数>
for i in $(seq 1 15); do
  SIZE=$(stat -c%s "$FILE" 2>/dev/null || echo 0)
  [ "$SIZE" -ge "$EXPECTED" ] && break
  curl -L -C - --http1.1 --retry 5 --retry-delay 2 --max-time 300 -o "$FILE" "$URL"
done
```

ダウンロード後は必ずファイルサイズ（`HEAD`リクエストの`content-length`と比較）とマジックバイト
（先頭4バイトが`GGUF`）を確認すること。

## モデル選定について調べた内容

Qwen2.5-0.5B/1.5B-Instruct以外に、4B未満クラスで候補として調査済み（詳細は会話履歴参照、要点のみ）：

| モデル | パラメータ | ライセンス | 日本語 | 備考 |
|---|---|---|---|---|
| Qwen3-4B-Instruct-2507 | 4B | Apache 2.0 | ◎ | non-thinking固定版。無印Qwen3系はthinkingモードがあるため2507版を推奨 |
| Gemma3-4B-it | 4B | Gemma利用規約 | ◎ | マルチモーダル対応。当初`WinJevlyn.MultimodalPlayground`で検証予定だったが、最終的にQwen3-VL-8B-Instructに切り替え |
| Phi-4-mini-instruct | 3.8B | MIT | △ | 数学・推論は強いが日本語はQwen/Gemmaに劣る |
| Llama-3.2-3B-Instruct | 3B | Llama3.2コミュニティライセンス | △ | |
| **Sarashina2.2-3B-instruct** | 3B | MIT（商用可） | ◎◎ 日本語特化 | SB Intuitions製。ユーザーがLM StudioでQ4_K_S版をDL済み・検証予定 |
| TinySwallow-1.5B-Instruct | 1.5B | 研究用途限定（商用不可） | ◎ | Sakana AI製、Qwen2.5-32B蒸留 |

Sarashina2.2はLlamaベースアーキテクチャなので、LLamaSharp/llama.cppで問題なく動く（追加対応不要）。

## WinJevlyn.MultimodalPlayground（マルチモーダル検証用の別プロジェクト）

Gemma3-4B-itのような画像入力対応モデルを試すために作成した、本体`WinJevlyn`とは**意図的に分離した**
独立プロジェクト。分離した理由：

- LLamaSharpのマルチモーダル対応（`MtmdWeights` / `SafeMtmdModelHandle` / `SafeMtmdInputChunks` など）は、
  本体が使っている `LLamaWeights` / `LLamaContext` の単純tokenize→decode経路とは完全に別のAPI群であり、
  「変更箇所が極小」では済まない規模の実装差分になるため。
- 本体は「テキスト3択の単一フォワードパスロジット抽出」という単機能に絞った設計であり、画像入力を混ぜると
  ドメインモデル（`ChoiceResult`/`InferenceEngine`はテキスト前提）が複雑化する。
- 検証済みで動作する本体を壊すリスクを避けるため。

実装は公式サンプル `MtmdInteractiveModeExecute.cs`（LLamaSharp v0.27.0）のパターンに準拠し、
`InteractiveExecutor(context, clipModel)` に画像embedding（`clipModel.LoadMedia(path)`）を渡して
高レベルAPIで推論する方式（低レベルの`EvaluateChunks`等を自前で呼ばない）。後に対象モデルを
Gemma3-4B-itからQwen3-VL-8B-Instructへ切り替え、CUDA12バックエンド・タイミング計測を追加した。

## WinJevlyn.Multimodal（マルチモーダル版GUI、GPU優先・CPU自動フォールバック）

当初は自由文の質問に対してストリーミングで文章生成する実装（`InteractiveExecutor`ベース）だったが、
本体`WinJevlyn`と同じ、1回のフォワードパスで選択肢A/B/Cの確率を直接取得する方式が求められ、
`LLama.Batched.BatchedExecutor` + `Conversation` APIを使う実装に全面的に書き換えた。
対象モデルはQwen3-VL-8B-Instruct（本体GGUF + mmprojの2ファイル構成）。

### 画像対応の単一フォワードパスロジット抽出（`BatchedExecutor`使用）

`InferenceEngine`（テキスト専用）の`LLamaBatch.AddRange(..., logitsLast: true)` +
`context.NativeHandle.GetLogitsIth(...)`という直接操作は、画像embeddingを含むプロンプトには使えない
（画像はmtmdの`SafeMtmdInputChunks`という別経路で処理されるため）。代わりに`LLama.Batched`名前空間の
高レベルAPIを使う：

1. `new BatchedExecutor(weights, modelParams, clipModel)` — 内部で`LLamaContext`を保持する
   （`executor.Context`でアクセス可能）。`weights.CreateContext(...)`を自前で呼ぶ必要はない。
2. 推論1回ごとに`executor.Create()`で新しい`Conversation`を作り、使い終わったら`Dispose()`する
   （呼び出しごとにKVキャッシュのシーケンスIDが新規発行されるため、`InferenceEngine`のような
   `MemoryClear(true)`は不要）。
3. `conversation.Prompt(promptText, new[] { embed }, addBos: true)`でプロンプト（画像マーカー含む
   テキスト）と画像embeddingをまとめて予約し、`await executor.Infer()`で1回だけデコードする。
4. `conversation.Sample(0)`が生の logits（`Span<float>`）を返す — サンプリングは行わず、ここから
   A/B/Cのトークンに対応するlogitだけを取り出してsoftmaxする（`InferenceEngine`と同じロジック）。
5. A/B/Cの候補トークンIDの特定（差分トークナイズ方式）は、画像マーカーを含むフルプロンプトに対して
   プレーンな`executor.Context.Tokenize(...)`を使えばよい。BPEトークナイズは局所的なので、
   「Answer: 」の直後の境界は離れた場所にある画像マーカーの影響を受けない。

実測（GPU、RTX 4060 Ti 1枚固定）: 実写真で1,734ms（初回・CUDAグラフのウォームアップ込み）、
2回目以降は155ms程度まで短縮。いずれも正解の選択肢を99%以上の確信度で選択できることを確認済み。

### GPU/CPUフォールバックは2階層

1. **ネイティブライブラリ選択（プロセスで1回だけ）**: `NativeLibraryConfig.All.WithCuda(true).WithAutoFallback(true)`。
   一度選択したネイティブライブラリはプロセス内で変更できないため、モデルを読み込むたびに呼ばず、
   `MultimodalEngine`内でstaticフラグにより一度だけ実行する。
2. **モデル単位のフォールバック（読み込みごと）**: まず`GpuLayerCount=99`で読み込みを試み、例外
   （VRAM不足でこの特定モデルが乗らない場合など）が出たら`GpuLayerCount=0`で同じネイティブライブラリの
   まま再試行する。

### 「実際にGPUが使われたか」の判定に関する落とし穴（`NativeLibraryConfig.All.DryRun`は使わない）

当初、`NativeLibraryConfig.All.DryRun(out var loadedLLama, out _)`を実行し、
`loadedLLama?.Metadata?.UseCuda`で「そもそもCUDA対応ライブラリが選択されたか」を先に確認してから
GPU読み込みを試みるかどうかを決める実装にしていたが、**これがバグの原因になった**。

実際にこのマシン（GPU 2枚搭載、正常にCUDAが使える環境）でスクラッチパッドから直接検証したところ、
`DryRun`は`true`（成功）を返すにもかかわらず、out引数の`loadedLLama`が`null`になり、
`Metadata?.UseCuda`が常に`false`と誤判定された。結果として`MultimodalEngine`がGPU読み込みを
一度も試みずCPUにフォールバックし続けるというバグを引き起こした（ユーザー報告: 「バックエンドがCPUに
なります」）。

**対策**: `DryRun`による事前チェックは廃止し、[WinJevlyn.MultimodalPlayground](src/WinJevlyn.MultimodalPlayground)が
最初から採用していた「直接GPUパラメータで読み込みを試み、例外が出たらCPUにフォールバックする」という
単純な方式に統一した。`GpuLayerCount=99`を渡した読み込みはCPU専用ネイティブライブラリでも例外を出さず
黙って成功する（n_gpu_layersが内部で無視されるだけ）ため、「GPUが全く無い環境でもGPUと誤表示される」
という逆方向の誤判定リスクは理論上残るが、実際にGPUがあるのにCPUと誤判定される（今回発生した方の）
バグよりは実害が小さいと判断し、シンプルさを優先した。

**教訓**: LLamaSharpの高レベルな診断API（`DryRun`等）を過信せず、実機・実データでの検証を必ず行うこと。
このバグも「コードは正しそうに見えるが実際に動かすと違う」典型例だった。

### CUDAバックエンドのダウンロードサイズについて（誤解の訂正）

当初「CUDAランタイムを含むため本体より大幅にサイズが大きくなる」と想定していたが、誤りだった。
`LLamaSharp.Backend.Cuda12`のネイティブDLL（`ggml-cuda.dll`等）自体は約8.6MB程度で、CUDA Toolkitの
ランタイム（cuBLAS等）は同梱せず、対象マシンのNVIDIAドライバに依存する方式。単一exe発行時のサイズ増加は
軽微な見込み（実測はまだ）。

### 重要: マルチGPU環境では`SplitMode = GPUSplitMode.None` + `MainGpu = 0`が必須

`GpuLayerCount=99`だけを指定すると、llama.cppはデフォルトで**複数GPUに自動的にモデルを分割**する
（`GPUSplitMode.Layer`相当の既定動作）。このマシン（RTX 4060 Ti + RTX 5060 Ti の2枚構成）で実測した結果：

| 構成 | 画像処理＋プロンプト処理 | テキスト生成速度 |
|---|---|---|
| 2GPU自動分割（既定） | 63,083 ms | 17.3 tok/s |
| **1GPU固定**（`SplitMode=None`, `MainGpu=0`） | **384 ms**（約164倍高速） | **50.6 tok/s**（約3倍高速） |

8Bクラスの比較的小さいモデルを複数GPUに分割すると、PCIe経由の同期オーバーヘッドで大幅に遅くなる。
配布先の多くはGPU1枚構成と想定されるため、`MultimodalEngine.cs`のGPU読み込みパスでは明示的に
`SplitMode = GPUSplitMode.None`, `MainGpu = 0`を指定している（マルチGPU環境でも1枚目のGPUだけを
使う）。この設定をしないと、マルチGPU環境を持つユーザーほど体感速度が悪化するという逆説的な結果になる。

## 配布方法（2種類）

用途に応じて2通りの配布方法を用意している。どちらも [README.md](README.md) に手順あり。

1. **単一exe配布**（`publish.bat`）: 自己完結・単一ファイルの`WinJevlyn.exe`をルート直下の
   `publish/`に生成する。相手先に.NET 8のインストールは不要。`-p:DebugType=None`で`.pdb`は生成しない
   （実行時に不要なため）。署名なしなので初回起動時にSmartScreen警告が出る点に注意。
2. **ソース一式配布（Visual Studioでビルド）**: `NuGet.config`（リポジトリルート）でパッケージ取得元を
   `nuget.org`に固定しているため、Visual Studio 2022（.NETデスクトップ開発ワークロード）で
   `WinJevlyn.sln`を開いてビルドするだけで、`LLamaSharp`/`LLamaSharp.Backend.Cpu`が自動的に
   NuGetから復元される。手動でDLLを集めて同梱する必要はない。
   - 検証済み: `NUGET_PACKAGES`環境変数で完全に空のパッケージキャッシュを指定した状態
     （＝初めてこのマシンを使う人を模した状態）から`dotnet restore`→`dotnet build`が成功することを確認
     （nuget.orgから約187MBを新規ダウンロード）。
3. **単一exe配布（マルチモーダル版）**（`publish-multimodal.bat`）: `WinJevlyn.Multimodal.exe`を
   `publish-multimodal/`に生成する。本体用の`publish.bat`を流用せず別スクリプトにしたのは、
   出力先フォルダを分けて2つの配布物を混同しないため。

## リネーム作業のメモ（Jev → WinJevlyn）

2026-10-01頃、「Jev」が他社製品名と衝突するため全面リネームを実施。作業内容：

- フォルダ・ファイルは `git mv` でリネームし、履歴を保持（`src/JevInferenceApp` → `src/WinJevlyn` 等）
- namespace/クラス名も追従（`JevInferenceEngine` → `InferenceEngine`、`JevMultimodalEngine` → `MultimodalEngine`）。
  新しいnamespaceが`WinJevlyn`/`WinJevlyn.Multimodal`となるため、クラス名に重ねて`Jev`を残す必要が
  なくなった。
- `WinJevlyn.Multimodal` / `WinJevlyn.MultimodalPlayground` はドット区切りの命名（`RootNamespace`/
  `AssemblyName`にドットを含む）を採用。.NET のマルチプロジェクトソリューションでは一般的な命名規則で、
  発行後のexeファイル名もそのまま `WinJevlyn.Multimodal.exe` になる。
- リネーム後、スクラッチパッドから`MultimodalEngine`を直接呼び出す形で実写真の分類が正しく動作すること
  （GPU使用、黒と黄色を100%で正解）を再検証し、リネームによる機能面の破損がないことを確認済み。

## 現在の状態・次にやること

- [WinJevlyn](src/WinJevlyn) 本体: 実装・ビルド・実推論検証済み。単一exe発行・ソース配布
  （VS向けNuGet自動復元）の両方を検証済み。「Jev」からのリネーム後もビルド・起動・実推論を再検証済み。
- [WinJevlyn.MultimodalPlayground](src/WinJevlyn.MultimodalPlayground): CUDA12バックエンド追加・
  タイミング計測追加済み。Qwen3-VL-8B-Instruct + mmprojで実際にGPU推論を実行し、
  `SplitMode=None`/`MainGpu=0`固定で画像処理384ms・生成50.6 tok/sを実測済み（詳細は本ファイルの
  該当節を参照）。
- [WinJevlyn.Multimodal](src/WinJevlyn.Multimodal): `BatchedExecutor`ベースの単一フォワードパス3択判定
  （画像＋文脈＋選択肢A/B/C→1回のフォワードパスで確率算出）に全面書き換え済み。Debug/Release両構成で
  ビルド確認・起動クラッシュ確認済み。スクラッチパッドから`MultimodalEngine`を直接呼び出し、
  実写真・合成画像の両方で正解選択肢を99%以上の確信度で判定できることを実データ・実GPUで確認済み。
  GUIのボタン操作を伴うE2E確認はツール制約により未実施（起動クラッシュ確認とエンジン直接呼び出しでの
  実証で代替）。
- Sarashina2.2-3B-instruct（Q4_K_S、ユーザーがLM Studioでダウンロード済み）での動作検証は未実施。
