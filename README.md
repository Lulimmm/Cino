# 自动挖宝

这是一个面向 Dalamud API 15 的自动挖宝插件，包含车头、车轮、补图和寻路逻辑。

## 构建

```powershell
dotnet publish .\AutoTreasureHunt.csproj -c Release -o .\dist\AutoTreasureHunt
Compress-Archive -Path .\dist\AutoTreasureHunt\* `
  -DestinationPath .\AutoTreasureHunt.zip -Force
```

构建输出的 DLL 位于：

```text
dist\AutoTreasureHunt\AutoTreasureHunt.dll
```

项目已经将 DLL 的程序集版本固定为 `1.0.3.0`，并与 `pluginmaster.json` 中的
`AssemblyVersion` 保持一致。自定义仓库校验时这两个版本必须一致。

## 自定义仓库

`pluginmaster.json` 是符合 Dalamud 自定义仓库格式的 JSON 数组，可以直接作为
仓库 URL 发布。它必须通过无需认证的 HTTP GET 访问，并且其中的
`DownloadLinkInstall`、`DownloadLinkUpdate` 和 `DownloadLinkTesting` 必须指向可下载的
插件 ZIP 文件。

发布新版本时需要同步更新：

1. `AutoTreasureHunt.csproj` 的版本号；
2. `pluginmaster.json` 的 `AssemblyVersion` 和下载链接版本；
3. 更新并公开托管仓库根目录的 `AutoTreasureHunt.zip`。

在 Dalamud 设置的“第三方插件/自定义插件仓库”中添加托管后的
`pluginmaster.json` URL，然后刷新插件列表即可。当前仓库地址为：

```text
https://raw.githubusercontent.com/Lulimmm/Cino/refs/heads/main/pluginmaster.json
```

## 本地加载

本地测试请选择 `dist\AutoTreasureHunt\AutoTreasureHunt.dll`。与 DLL 同目录的
`AutoTreasureHunt.json` 是本地插件清单，必须保持为单个 JSON 对象。如果修改了插件或依赖，
请先卸载旧版本，再重新加载 DLL。

项目默认从以下目录引用 Dalamud API：

```text
C:\Users\admin\.nuget\packages\aeassist.net\1.2.15\lib\net10.0
```

如需更换 API 引用目录，可在构建时指定：

```powershell
dotnet publish -c Release -p:DalamudLibPath="其他目录"
```
