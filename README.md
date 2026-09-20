# 雷霆三角洲量化系统（开源版）

> 让每一颗子弹，都为你打工。

《三角洲行动》首款**可视化**子弹倒卖自动化引擎：拖节点即成流程，64 条秒级行情、DeepSeek AI 选品、21 套现成方案、7×24 无人值守。

- 官网：<https://leitingquant.bond>
- 下载官方发布版（需激活码）：<https://leitingquant.bond>
- QQ 群：3245668977
- 邮箱：LeiTingQuant@126.com

---

## 项目结构

```
├── WindowSpy/          主程序（.NET 8 + WPF，C#）
│   ├── FlowModel.cs        可视化流程节点/连线数据模型（40+ 节点定义）
│   ├── FlowRunner.cs       流程执行引擎（错误暂停/循环栈/变量系统）
│   ├── FlowEditorWindow.*  流程工坊可视化编辑器（拖拽/框选/自动排版/我的模块）
│   ├── FlowTemplates.cs    21 套内置方案模板（AI/脚本/多品种）
│   ├── FlowLayout.cs       分叉-汇合自动排版算法
│   ├── MarketService.cs    moligod 官方行情接口（64 品种秒级现价+分钟K线）
│   ├── QuantMath.cs        九大量化指标（MA/EMA/RSI/MIN/MAX/CHANGE/VOL/POSITION/LAST）
│   ├── DeepSeekClient.cs   DeepSeek AI 决策节点（BUY/WAIT/SELL）
│   ├── HostActions.cs      鼠标键盘模拟执行层
│   ├── MonitorWindow.*     屏幕监控/OCR 视觉
│   └── ...
└── Scripts/            Python OCR 脚本（ONNX 文字识别）
```

## 核心特性

- **流程工坊**：七大类 40+ 可视化节点，拖拽编排交易策略，出错红框暂停可续跑
- **我的模块**：框选节点打包复用，递归嵌套，`.subflow.json` 即资产
- **行情与 AI**：64 品种秒级行情直连官方接口，九大量化指标，DeepSeek AI 决策/是/否/三选一节点
- **六大策略**：SMART / PROFIT / RISE / DIP / REBOUND / RANGE 选品引擎
- **21 套模板**：新手测试到 AI 量化全自动闭环，开箱即用
- **安全风控**：双层封禁检测、出错即停热修复、窗口 A/B 双开、F12 急停

## 构建

- Visual Studio 2022 或 .NET 8 SDK
- `dotnet build WindowSpy`（Windows 平台，net8.0-windows）

> Python OCR 环境与 ONNX 模型文件不在仓库内（体积原因），OCR 节点需自行用
> `Scripts/` 下的脚本打包，或直接使用官方发布版。

## 关于授权模块（未开源说明）

本仓库为**源码学习用途**。授权/加密算法相关代码（机器指纹、ECDSA P-256 签名验证、
申请码编解码、防篡改校验）**未包含在本仓库**，`WindowSpy/LicenseManager.cs`
为占位实现，仅保留公开接口使项目可编译。

因此：

- 本仓库编译产物**无法激活**，仅供阅读与研究
- 完整可用的软件请从[官网](https://leitingquant.bond)下载官方发布版（需激活码授权）

## 免责声明

本软件仅通过模拟鼠标键盘实现重复操作自动化，与游戏官方无任何关联。
请理性游戏、遵守用户协议；因使用本工具产生的账号风险由使用者自行承担。

## License

MIT © 雷霆网络开发工作室
