# ExternalPeepSight

Windows 外置准星软件。

## 文档

- [需求规格说明](docs/需求文档.md)
- [技术实现方案](docs/技术实现方案.md)
- [项目实施计划](docs/项目实施计划.md)

## 技术栈

- 配置界面：C#、.NET 10、Avalonia 12 Desktop、FluentAvalonia 3.x、MVVM
- 覆盖引擎：C++20、Win32、Direct3D 11、Direct2D、DirectWrite、WIC、DirectComposition
- 构建：.NET CLI、CMake、MSVC、Windows SDK

UI 初始目标基线为 Avalonia 12.1.0 和 FluentAvaloniaUI 3.0.2。应用只使用 `FluentAvaloniaTheme`，不与 `Avalonia.Themes.Fluent` 混用；Avalonia 官方包保持相同版本，所有包使用固定版本。

## 工程结构

```text
docs/                         需求和技术方案
src/ExternalPeepSight.UI/     配置界面（当前为待迁移的 WPF 骨架）
src/ExternalPeepSight.Core/   跨 UI 和 Host 的领域模型
src/ExternalPeepSight.Host/   原生覆盖引擎
tests/                         .NET 单元测试
```

## 构建

在 PowerShell 中运行：

```powershell
.\build.ps1
```

指定 Release：

```powershell
.\build.ps1 -Configuration Release
```

只构建托管项目：

```powershell
.\build.ps1 -SkipNative
```

只构建原生 Host：

```powershell
.\build.ps1 -SkipManaged
```

原生 Host 当前是空入口，用于验证 CMake、MSVC、Windows SDK 和 DirectX 链接环境。

## 检查

日常提交前检查：

```powershell
.\check.ps1
```

增加 .NET 覆盖率门槛检查：

```powershell
.\check.ps1 -Coverage
```

执行完整检查，包括覆盖率、MSVC 静态分析和 AddressSanitizer：

```powershell
.\check.ps1 -Coverage -Deep
```

当前检查标准：

- .NET 推荐级分析器和代码风格检查；
- 所有托管与原生编译警告视为错误；
- Core 行覆盖率和分支覆盖率均不低于 85%；
- NuGet 直接和传递依赖漏洞检查；
- C++ 使用 `/W4`、`/permissive-`、`/sdl` 和 UTF-8 编译；
- C++ 使用 GoogleTest、CTest 和 clang-format；
- 深度检查使用 MSVC `/analyze` 和 AddressSanitizer。
