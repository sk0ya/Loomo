# CSharpIde fixture

`§33.5 Phase 0` の再実行用ワークスペース。次の経路を一つの小さな solution で確認する。

- `Contracts` → `Feature` の ProjectReference
- `Feature` の `net9.0`／`net10.0` multi-targeting
- `Shared/LinkedFile.cs` の linked file
- `FixtureGenerator` を Analyzer として参照する Source Generator
- `Client` の WPF プロジェクト
- `Directory.Build.props`／`Directory.Build.targets`／`.editorconfig`／`stylecop.json`
- `Feature.Tests` のテストプロジェクト
- xUnitの実テストを使った `dotnet test`／`--list-tests` の公式test adapter経路

## Build

通常のビルドでは、意図的な `SA1101` 違反が error になるため失敗する。これは IDE と Build の
severity を同じfixtureで確認するためのもの。

```powershell
dotnet build CSharpIde.sln --nologo
```

ビルド成功経路は次のとおり。

```powershell
dotnet build CSharpIde.sln --nologo -p:NoWarn=SA1101
```

テスト検出・実行の確認は次のコマンドで行う。

```powershell
dotnet test tests/Feature.Tests/Feature.Tests.csproj --nologo --list-tests -p:NoWarn=SA1101
dotnet test tests/Feature.Tests/Feature.Tests.csproj --nologo -p:NoWarn=SA1101
```

solution構成の切替確認は次のとおり。

```powershell
dotnet build CSharpIde.sln -c Release --no-restore --nologo -p:NoWarn=SA1101
dotnet test tests/Feature.Tests/Feature.Tests.csproj -c Release --no-restore --nologo -p:NoWarn=SA1101
```

この fixture の生成コードは `bin/` に出るため、ソースツリーへは追加しない。
