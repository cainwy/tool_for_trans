# ClipTranslateOnce

单次调用的 Windows 低资源浮层翻译工具。

程序不会常驻后台，不注册全局热键，不轮询系统状态。每次被调用时读取输入文本，调用翻译后端，然后在鼠标当前位置附近显示置顶无激活浮层。手动点击 `×` 后进程退出。

## 推荐用法

### 1. 从剪切板读取

```powershell
ClipTranslateOnce.exe --clipboard
```

这是最稳定的日常方式：先手动复制文本，再调用程序。

### 2. 从标准输入读取

```powershell
"This is a test." | ClipTranslateOnce.exe --stdin
```

长文本、多行文本、含引号/括号/换行/特殊符号的文本，优先使用 stdin：

```powershell
@'
This is a long text.
It may contain "quotes", brackets ( ), and line breaks.
'@ | ClipTranslateOnce.exe --stdin
```

### 3. 从文件读取

```powershell
ClipTranslateOnce.exe --file .\input.txt
```

这是最适合论文段落、长文本、脚本生成内容的方式。

### 4. 直接传入短文本

```powershell
ClipTranslateOnce.exe --text "This is a short sentence."
```

不建议用这种方式传很长的文本，因为 Windows 命令行长度、Shell 转义、引号和换行都可能造成问题。

## 常用参数

```text
--stdin                 从标准输入读取
--file <path>           从 UTF-8 文件读取
--text <text>           从命令行参数读取短文本
--clipboard             读取剪切板
--selected              模拟 Ctrl+C 读取当前选中文本
--auto                  先尝试 selected，失败后读取 clipboard
--target <lang>         设置目标语言，例如 ZH-HANS、EN-US、JA
--source <lang>         设置源语言；留空时由翻译服务自动检测
--provider <name>       deepl | libretranslate | echo
--print                 同时输出译文到控制台
--no-overlay            不显示浮层，只配合 --print 使用
--raw                   不合并 PDF 复制产生的断行
--config <path>         指定配置文件路径
--help                  显示帮助
```

## 配置 DeepL

推荐使用环境变量：

```powershell
setx DEEPL_API_KEY "你的 DeepL API Key"
```

重新打开终端后生效。

也可以编辑 `appsettings.json`，但不建议把 API key 提交到 GitHub。

## GitHub Actions 编译

仓库已经包含 `.github/workflows/build.yml`。

触发方式：

- push 到 `main` 或 `master`
- push `v*` 标签
- pull request
- 手动 workflow_dispatch

编译产物会作为 Actions artifact 上传，包含两个 ZIP：

- `ClipTranslateOnce-win-x64-framework-dependent.zip`
- `ClipTranslateOnce-win-x64-self-contained.zip`

framework-dependent 版本体积较小，但目标机器需要安装 .NET 8 Desktop Runtime。self-contained 版本体积较大，但不依赖目标机器预装运行时。

## 本地编译

```powershell
.\scripts\build-local.ps1
```

或者手动执行：

```powershell
dotnet publish src/ClipTranslateOnce/ClipTranslateOnce.csproj -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true -o publish
```

## 贴图/浮层实现说明

本程序没有使用 Electron、WebView 或 OCR。浮层是 WinForms + Win32 扩展窗口样式实现的置顶、无激活、工具窗口。它不是截图工具意义上的真实图片层，但资源占用更低，也更适合显示可复制文本。

右键点击译文区域会复制译文并关闭浮层。点击右上角 `×` 关闭浮层。
