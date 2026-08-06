# 自动挖宝

这是一个引用 Dalamud 的最小 .NET 10 类库项目。

## 构建

```powershell
dotnet build .\AutoTreasureHunt.csproj -c Release
```

生成的插件程序集位于：

```text
bin\Release\net10.0-windows\AutoTreasureHunt.dll
```

项目默认从以下目录引用 `Dalamud.dll`：

```text
C:\Users\admin\.nuget\packages\aeassist.net\1.2.15\lib\net10.0
```

如果程序集目录发生变化，可以在构建时覆盖它：

```powershell
dotnet build -c Release -p:DalamudLibPath="其他目录"
```
