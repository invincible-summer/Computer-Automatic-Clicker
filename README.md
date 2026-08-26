# 宏连点器 · Macro Clicker

一个 Windows 桌面自动化工具：**录制** 鼠标/键盘操作 → **编辑** 事件 → **循环回放**。
基于 .NET 10 + WinForms，纯 Win32 API（低级全局钩子 + SendInput），无第三方依赖。

## 功能

- **宏录制**：捕获鼠标点击（左/右/中/侧键）、拖拽轨迹、滚轮、键盘按键与组合键（如 `Ctrl+C`）
- **间隔记录**：每个事件记录与上一事件的间隔（delta time），回放时还原真实操作节奏
- **事件编辑**：双击列表行可修改间隔、坐标、按键；支持删除、上移/下移、复制、插入新事件
- **循环执行**：执行一次 / 指定次数 / 无限循环，支持循环间隔
- **播放速度**：0.25x ~ 8x
- **播放前倒计时**
- **紧急停止**（fail-safe）：鼠标猛甩到屏幕左上角立即停止
- **JSON 保存/加载**：宏文件为可读 JSON，可手工修改
- **全局热键**：无需聚焦程序窗口即可控制

## 全局热键

| 按键 | 功能 |
| --- | --- |
| `F6` | 开始 / 停止录制 |
| `F7` | 停止录制 |
| `F8` | 开始执行（暂停中则为继续） |
| `F9` | 暂停 / 继续 |
| `F10` | 停止一切（录制与执行） |

热键在录制时不会被录进宏里。循环执行卡住时，**F10** 或把鼠标移到**屏幕左上角**即可急停。

## 使用方法

1. 双击 `publish\MacroClicker.exe`（或在 IDE 中运行项目）
2. 按 `F6` 开始录制，去目标窗口完成你想自动化的操作
3. 按 `F7` 结束录制，列表中可双击任意行微调（改间隔、坐标、按键等）
4. 设置执行模式 / 次数 / 速度，按 `F8` 执行
5. 点「保存」把宏存为 JSON，下次点「打开」直接复用

宏文件与界面设置保存在 exe 旁边的 `macros\` 目录。

## 录制选项说明

| 选项 | 默认 | 说明 |
| --- | --- | --- |
| 记录键盘输入 | 开 | 按键与组合键（Ctrl/Shift/Alt/Win + 键） |
| 记录鼠标点击 | 开 | 按下+释放合并为一次点击；按住移动超过阈值记为拖拽 |
| 记录滚轮 | 开 | 每格 ±120 |
| 记录拖拽 | 开 | 拖拽会记录按下点和移动轨迹（事件量较大） |
| 记录空闲鼠标移动 | 关 | 仅在需要还原精确鼠标轨迹时开启 |

## 从源码构建

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)（仅开发时需要，成品 exe 自包含运行时）：

```bash
# 调试运行
dotnet run --project src/MacroClicker

# 编译
dotnet build src/MacroClicker -c Release

# 发布为单文件 exe（输出到 publish\）
dotnet publish src/MacroClicker -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish
```

## 项目结构

```
src/MacroClicker/
├── Program.cs          # 入口
├── MainForm.cs         # 主窗口（工具栏 / 事件列表 / 设置面板 / 全局热键）
├── EventEditForm.cs    # 事件编辑对话框
├── Recorder.cs         # 录制器：原始输入 → 语义化事件（点击/组合键/拖拽）
├── Player.cs           # 回放引擎：循环 / 倍速 / 暂停 / 急停
├── GlobalHook.cs       # 全局键盘/鼠标低级钩子（WH_*_LL）
├── Simulator.cs        # SendInput 输入模拟
├── KeyMap.cs           # 虚拟键码 <-> 可读名称
├── MacroEvent.cs       # 事件模型
├── MacroStore.cs       # 宏 JSON 保存/加载、设置持久化
└── Win32.cs            # Win32 P/Invoke 声明
```

## 宏文件格式

```json
{
  "name": "我的宏",
  "version": 1,
  "events": [
    { "type": "mouse_click", "button": "left", "x": 800, "y": 450, "delay": 0 },
    { "type": "key", "key": "enter", "delay": 0.8 },
    { "type": "hotkey", "keys": ["ctrl", "c"], "delay": 0.3 },
    { "type": "wheel", "x": 800, "y": 450, "delta": -120, "delay": 0.5 },
    { "type": "wait", "delay": 1.0 }
  ]
}
```

`delay` = 执行该事件前需等待的秒数。事件类型：`mouse_click` / `mouse_down` / `mouse_up` / `move` / `wheel` / `key` / `hotkey` / `wait`。

## 注意事项

- 点击使用**屏幕绝对坐标**，回放前请保持目标窗口位置与录制时一致
- 管理员权限窗口中的操作可能无法被录制/回放（钩子与 SendInput 权限限制），必要时以管理员身份运行本程序
- 部分游戏使用驱动级输入或反作弊系统，可能拦截模拟输入
