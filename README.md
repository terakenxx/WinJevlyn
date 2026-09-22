# Jev Inference App

Windows 11 向けの軽量ローカル推論 GUI。特化型 AI「Jev」の思想（テキストを逐次生成せず、フォワードパス直後の
Logits から特定選択肢の確率を直接取得する）を模倣し、GGUF 形式の軽量モデル（Qwen2.5-0.5B / 1.5B-Instruct 等）を
CPU 単体で動かして、与えられた文脈と選択肢 A/B/C のうちどれが最も適切かを 1 回のフォワードパスで判定します。

- 実装: C# / .NET 8 / WPF + [LLamaSharp](https://github.com/SciSharp/LLamaSharp)（llama.cpp バインディング）
- 推論: CPU 単体、AVX2 最適化バックエンド、8 スレッド並列
- 配布: `dotnet publish` の Single-file 機能で `.exe` 1 本にまとめられます（モデルの `.gguf` は別ファイル）

## 動作原理（ロジック概要）

1. 入力文章と選択肢 A/B/C から、モデルの chat template（GGUF に埋め込まれていればそれを使用、無ければ
   プレーンテキストにフォールバック）でプロンプトを構築し、末尾を `Answer: ` で終える。
2. プロンプトをトークナイズし、1 回だけ `llama_decode` を実行（テキスト生成は行わない）。
3. 最後のトークン位置の next-token logits を取得し、"A" / "B" / "C" に対応するトークン ID の logit だけを
   抜き出して softmax（3 択の中だけで正規化）することで、各選択肢の確信度（%）を得る。
4. これにより、通常の自己回帰生成（1 トークンずつ生成→デコード）を行わずに、1 回のフォワードパスで
   3 択分類の確率分布を得られる（Jev の高速判定思想を模倣）。

該当コード: [`Services/JevInferenceEngine.cs`](src/JevInferenceApp/Services/JevInferenceEngine.cs)

## 前提条件

- Windows 11
- **.NET 8 SDK**（このマシンには現在 .NET 5 系のみ入っているため、ビルドには別途インストールが必要です。
  `winget install Microsoft.DotNet.SDK.8` または https://dotnet.microsoft.com/download/dotnet/8.0 から入手）
- GGUF モデルファイル（例: `qwen2.5-0.5b-instruct-q4_k_m.gguf`）を別途用意してください。本リポジトリには
  モデルファイルは含まれません。

## ビルド・実行（開発時）

```bash
dotnet restore JevInferenceApp.sln
dotnet run --project src/JevInferenceApp/JevInferenceApp.csproj
```

`LLamaSharp.Backend.Cpu` パッケージが AVX/AVX2/AVX512 各バックエンドのネイティブ DLL を自動的に出力先へコピーする
ため、`dotnet run` の時点では単一ファイル化しなくてもそのまま動作します。

## 単一実行ファイルとして発行

```bash
dotnet publish src/JevInferenceApp/JevInferenceApp.csproj -c Release -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true
```

生成物は `src/JevInferenceApp/bin/Release/net8.0-windows/win-x64/publish/JevInferenceApp.exe` です。
このファイルと GGUF モデルファイルさえあれば、他の DLL を散乱させずに配布・実行できます。

## 画面の使い方

1. 「参照...」で GGUF ファイルを選択し、「モデル読み込み」を押す（数秒〜十数秒かかります）。
2. 「入力文章 (State / Context)」に判定させたい文脈を入力。
3. 「選択肢 A/B/C」にそれぞれのテキストを入力。
4. 「推論（判定）」を押すと、1 回のフォワードパスで各選択肢の確率が算出され、結果グリッドに表示されます。
   最も確率が高い行はハイライトされ、処理時間が `Latency: NN ms` として表示されます。
