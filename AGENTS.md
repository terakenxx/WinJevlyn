# AGENTS.md

このリポジトリで作業するエージェント（Claude Code等）向けの背景情報・設計判断・注意点をまとめたものです。
コードやREADMEから自明でない「なぜそうしたか」を中心に記載しています。

## プロジェクトの目的

特化型AI「Jev」の思想（テキストを逐次生成せず、フォワードパス直後のLogitsから特定選択肢の確率を
直接取得する）を模倣した、Windows 11向け軽量ローカル推論GUI。GGUF形式の軽量モデル（Qwen2.5系など）を
CPU単体で動かし、文脈と選択肢A/B/Cのうちどれが最も適切かを1回のフォワードパスで判定する。

詳細な動作原理・ビルド手順は [README.md](README.md) を参照。

## リポジトリ構成

```
JevInferenceApp.sln
src/
  JevInferenceApp/            本体（WPF GUI + Jev式ロジット抽出）
    Services/JevInferenceEngine.cs   コアロジック（このリポジトリで最も重要なファイル）
    MainWindow.xaml(.cs)             GUI
    Models/ChoiceResult.cs
  JevMultimodalPlayground/    マルチモーダル（画像入力）検証用の独立したコンソールアプリ
models/                        GGUFモデルの置き場（.gitignoreで除外、リポジトリには含めない）
```

## 開発環境に関する経緯

- このマシンには元々 .NET 5 系のSDKしか入っておらず、.NET 8 SDKを `winget install Microsoft.DotNet.SDK.8`
  でインストール済み（本体はWPFのため `net8.0-windows` が必須）。
- LLamaSharpのAPIは頻繁に変わるため、実装前に必ず該当バージョン（現在 `0.27.0` 固定）のソースを
  GitHub上で直接確認してから使うこと（`curl` で `raw.githubusercontent.com/SciSharp/LLamaSharp/v0.27.0/...`
  を取得するのが最も確実）。ドキュメントやブログの情報は古いAPIを指していることが多い。

## JevInferenceEngine の設計判断

[Services/JevInferenceEngine.cs](src/JevInferenceApp/Services/JevInferenceEngine.cs) の実装で、コードコメントには
書いていない意図：

- **1回のdecodeのみ**: `LLamaBatch.AddRange(tokens, 0, LLamaSeqId.Zero, logitsLast: true)` で
  最後のトークン位置だけlogitsを要求し、`context.Decode(batch)` を1回呼ぶだけ。テキスト生成ループは行わない。
  これが「Jev式」の核心部分。
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

1. スクラッチパッド配下に `JevInferenceApp.csproj` を `ProjectReference` する軽量コンソールアプリ
   （`EngineSmokeTest`）を作り、`JevInferenceEngine` を直接呼び出して実モデルで推論させ、標準出力で
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
| Gemma3-4B-it | 4B | Gemma利用規約 | ◎ | マルチモーダル対応。`JevMultimodalPlayground`で検証予定 |
| Phi-4-mini-instruct | 3.8B | MIT | △ | 数学・推論は強いが日本語はQwen/Gemmaに劣る |
| Llama-3.2-3B-Instruct | 3B | Llama3.2コミュニティライセンス | △ | |
| **Sarashina2.2-3B-instruct** | 3B | MIT（商用可） | ◎◎ 日本語特化 | SB Intuitions製。ユーザーがLM StudioでQ4_K_S版をDL済み・検証予定 |
| TinySwallow-1.5B-Instruct | 1.5B | 研究用途限定（商用不可） | ◎ | Sakana AI製、Qwen2.5-32B蒸留 |

Sarashina2.2はLlamaベースアーキテクチャなので、LLamaSharp/llama.cppで問題なく動く（追加対応不要）。

## JevMultimodalPlayground（マルチモーダル検証用の別プロジェクト）

Gemma3-4B-itのような画像入力対応モデルを試すために作成した、本体`JevInferenceApp`とは**意図的に分離した**
独立プロジェクト。分離した理由：

- LLamaSharpのマルチモーダル対応（`MtmdWeights` / `SafeMtmdModelHandle` / `SafeMtmdInputChunks` など）は、
  本体が使っている `LLamaWeights` / `LLamaContext` の単純tokenize→decode経路とは完全に別のAPI群であり、
  「変更箇所が極小」では済まない規模の実装差分になるため。
- 本体は「テキスト3択のJev式ロジット抽出」という単機能に絞った設計であり、画像入力を混ぜると
  ドメインモデル（`ChoiceResult`/`JevInferenceEngine`はテキスト前提）が複雑化する。
- 検証済みで動作するJevInferenceAppを壊すリスクを避けるため。

実装は公式サンプル `MtmdInteractiveModeExecute.cs`（LLamaSharp v0.27.0）のパターンに準拠し、
`InteractiveExecutor(context, clipModel)` に画像embedding（`clipModel.LoadMedia(path)`）を渡して
高レベルAPIで推論する方式（低レベルの`EvaluateChunks`等を自前で呼ばない）。

**現在の状態**: ビルドは通っているが、実際にGemma3-4B-it + mmprojファイルでの動作検証は**未実施**。
mmprojは本体GGUFとは別ファイルとしてダウンロードが必要（例: `unsloth/gemma-3-4b-it-GGUF`の
`mmproj-BF16.gguf`）。

## 配布方法（2種類）

用途に応じて2通りの配布方法を用意している。どちらも [README.md](README.md) に手順あり。

1. **単一exe配布**（`publish.bat`）: 自己完結・単一ファイルの`JevInferenceApp.exe`をルート直下の
   `publish/`に生成する。相手先に.NET 8のインストールは不要。`-p:DebugType=None`で`.pdb`は生成しない
   （実行時に不要なため）。署名なしなので初回起動時にSmartScreen警告が出る点に注意。
2. **ソース一式配布（Visual Studioでビルド）**: `NuGet.config`（リポジトリルート）でパッケージ取得元を
   `nuget.org`に固定しているため、Visual Studio 2022（.NETデスクトップ開発ワークロード）で
   `JevInferenceApp.sln`を開いてビルドするだけで、`LLamaSharp`/`LLamaSharp.Backend.Cpu`が自動的に
   NuGetから復元される。手動でDLLを集めて同梱する必要はない。
   - 検証済み: `NUGET_PACKAGES`環境変数で完全に空のパッケージキャッシュを指定した状態
     （＝初めてこのマシンを使う人を模した状態）から`dotnet restore`→`dotnet build`が成功することを確認
     （nuget.orgから約187MBを新規ダウンロード）。

## 現在の状態・次にやること

- [JevInferenceApp](src/JevInferenceApp) 本体: 実装・ビルド・実推論検証済み。単一exe発行・ソース配布
  （VS向けNuGet自動復元）の両方を検証済み。
- [JevMultimodalPlayground](src/JevMultimodalPlayground): ビルドのみ確認済み、実モデルでの動作検証は未実施。
- Sarashina2.2-3B-instruct（Q4_K_S、ユーザーがLM Studioでダウンロード中）での動作検証は未実施。
- Gemma3-4B-it + mmprojでのマルチモーダル動作検証は未実施。
