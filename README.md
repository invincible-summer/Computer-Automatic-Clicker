# 宏连点器 · Macro Clicker

跨平台自动化连点工具，一套设计思路、两个平台：

- **Windows 桌面端**（`src/MacroClicker`）：**录制** 鼠标/键盘操作 → **编辑** 事件 → **循环回放**。基于 .NET 10 + WinForms，纯 Win32 API（低级全局钩子 + SendInput），零第三方依赖，深色 / 浅色主题可切换。
- **Android 手机端**（`src/MacroClicker.Mobile`）：**屏幕取点** → **编辑序列** → **循环执行**。Kotlin 原生，通过无障碍服务（AccessibilityService）向任意应用注入点击 / 滑动手势，悬浮球随时启停，APK 可直接侧载安装。

## 手机版（Android）

> Android 出于安全限制无法录制其他应用的触摸操作，因此手机版采用与桌面端「配置 → 循环回放」等效的设计：
> **点哪取哪** 生成点击序列（或添加滑动），再以桌面端相同的执行模式循环回放。

### 功能

- **屏幕取点**：点哪加哪，连续取多个点击点，标记可单独删除
- **滑动手势**：依次取起点、终点，滑动时长可调
- **等待事件**：在序列中插入停留
- **循环执行**：执行一次 / 指定次数 / 无限循环，轮次间隔 + 开始倒计时——与桌面端一致
- **悬浮控制球**：贴边可拖动、位置记忆；展开面板即可开始 / 停止 / 取点，无需回到 App
- **宏互通**：可直接导入桌面端宏 JSON（点击 / 等待事件自动映射）
- **全机型适配**：全部界面使用 dp/sp 尺寸 + 比例定位；宏内记录屏幕分辨率，跨分辨率机型导入时坐标自动按比例换算；跟随系统深色模式

### 安装使用

1. 下载 APK（两种方式任选）：
   - **仓库离线包**：`publish/android/MacroClicker-v1.0.0-android.apk`，直接随仓库分发
   - **云构建最新版**：GitHub 仓库 → **Actions** → 选择 `Android APK` 工作流运行 → 下载 `MacroClicker-Android-APK` 产物
   安装到手机（需允许「安装未知应用」）
2. 打开 App，按引导完成两项授权：
   - **无障碍服务**（手势执行必需）：点击跳转系统设置开启「宏连点器」
   - **悬浮窗权限**（悬浮控制必需）：点击跳转授权「显示在其他应用上层」
3. 点「添加点击」→ App 退到后台 → 在目标界面点哪取哪 → 点「完成」
4. 展开悬浮球面板点「▶ 开始」即可循环执行，「■ 停止」随时停止

> 说明：APK 由 CI 每次构建时生成临时签名，更新安装若提示签名冲突，先卸载旧版再装。

### 无障碍服务用途声明

本服务的唯一职责是调用系统手势接口（`dispatchGesture`）按用户配置的坐标注入点击 / 滑动；**不读取、不记录任何屏幕内容**（`canRetrieveWindowContent=false`）。

### 手机版源码构建

需要 JDK 17 与 Android SDK（或 Android Studio 打开 `src/MacroClicker.Mobile` 直接构建）：

```bash
cd src/MacroClicker.Mobile
gradle assembleDebug     # 调试 APK
gradle assembleRelease   # Release APK
```

也可推送代码后由 GitHub Actions（`.github/workflows/android-apk.yml`）云端构建并上传 APK 产物。

## Windows 桌面端

### 功能

- **宏录制**：捕获鼠标点击（左/右/中/侧键）、拖拽轨迹、滚轮、键盘按键与组合键（如 `Ctrl+C`）
- **间隔记录**：每个事件记录与上一事件的间隔（delta time），回放时还原真实操作节奏
- **事件编辑**：双击列表行可修改间隔、坐标、按键；支持删除、上移/下移、复制、插入新事件
- **循环执行**：执行一次 / 指定次数 / 无限循环，支持循环间隔
- **播放速度**：0.25x ~ 8x
- **播放前倒计时**
- **紧急停止**（fail-safe）：鼠标猛甩到屏幕左上角立即停止
- **JSON 保存/加载**：宏文件为可读 JSON，可手工修改，与手机版互通
- **全局热键**：无需聚焦程序窗口即可控制
- **深色 / 浅色主题**：工具栏一键切换（☀/🌙），自动记忆
- **MuMu 模拟器内联**：ADB 直连模拟器执行点击，**不占用本机鼠标**（详见下文）

### MuMu 模拟器模式（不抢鼠标挂机）

右侧「模拟器 (MuMu · ADB)」卡片可将宏的执行目标切换为 MuMu 模拟器：

- **自动定位**：按 运行中的 MuMu 进程 → 默认安装目录 → 注册表卸载信息 的顺序查找官方接口 `MuMuManager.exe`（MuMu 12 V4.0.0+，位于安装目录 `shell\`），并优先使用其自带 `adb.exe`，避免系统 adb 版本冲突
- **实例识别**：通过 `MuMuManager info -v all` 拿到每个实例的 ADB 端口（MuMu 12 默认 16384，多开按 +32 递增；旧版 MuMu 6 为 7555）与主/渲染窗口句柄
- **自适应边框**：绑定**渲染子窗口**客户区，窗口移动、缩放、换边框比例都不影响坐标——屏幕坐标按当前渲染区实时换算为设备像素
- **不抢鼠标**：模拟器模式下，点击/滚动/按键经 ADB 注入（`input tap` / `input swipe` / `input keyevent`），本机鼠标完全空闲，可以同时做别的事；鼠标甩到屏幕左上角急停依然有效
- **截图取点**：一键截取模拟器画面，直接在截图上点选生成「device 坐标」点击事件（宏内标注 `emu`，存入 JSON 字段 `coordSpace: "device"`，不受窗口位置影响）
- **按键/滚轮**：常用按键映射为 Android keycode；滚轮以事件位置的小幅滑动近似（adb 无滚轮接口）；Ctrl/Shift/Alt 组合暂不支持（自动忽略并提示）

约束：单次 `adb input` 约有 200-500ms 系统延迟，模拟器模式建议事件间隔 ≥ 0.5s；首次连接若失败，先在模拟器设置里确认「ADB 调试」已开启，或重启模拟器后再连。

### 全局热键

| 按键 | 功能 |
| --- | --- |
| `F6` | 开始 / 停止录制 |
| `F7` | 停止录制 |
| `F8` | 开始执行（暂停中则为继续） |
| `F9` | 暂停 / 继续 |
| `F10` | 停止一切（录制与执行） |

热键在录制时不会被录进宏里。循环执行卡住时，**F10** 或把鼠标移到**屏幕左上角**即可急停。

### 使用方法

1. 双击 `publish\MacroClicker.exe`（或在 IDE 中运行项目）
2. 按 `F6` 开始录制，去目标窗口完成你想自动化的操作
3. 按 `F7` 结束录制，列表中可双击任意行微调（改间隔、坐标、按键等）
4. 设置执行模式 / 次数 / 速度，按 `F8` 执行
5. 点「保存」把宏存为 JSON，下次点「打开」直接复用

宏文件与界面设置保存在 exe 旁边的 `macros\` 目录。

### 录制选项说明

| 选项 | 默认 | 说明 |
| --- | --- | --- |
| 记录键盘输入 | 开 | 按键与组合键（Ctrl/Shift/Alt/Win + 键） |
| 记录鼠标点击 | 开 | 按下+释放合并为一次点击；按住移动超过阈值记为拖拽 |
| 记录滚轮 | 开 | 每格 ±120 |
| 记录拖拽 | 开 | 拖拽会记录按下点和移动轨迹（事件量较大） |
| 记录空闲鼠标移动 | 关 | 仅在需要还原精确鼠标轨迹时开启 |

### 从源码构建

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
src/
├── MacroClicker/            # Windows 桌面端（.NET 10 WinForms）
│   ├── Program.cs           # 入口
│   ├── MainForm.cs          # 主窗口（工具栏 / 事件列表 / 设置面板 / 全局热键）
│   ├── EventEditForm.cs     # 事件编辑对话框
│   ├── UiTheme.cs           # 主题系统：深/浅色板 + 自绘圆角按钮/卡片/输入框/列表
│   ├── Recorder.cs          # 录制器：原始输入 → 语义化事件（点击/组合键/拖拽）
│   ├── Player.cs            # 回放引擎：循环 / 倍速 / 暂停 / 急停 / 模拟器执行后端
│   ├── Emulator/            # MuMu 模拟器内联模块
│   │   ├── AdbClient.cs     # adb.exe 封装：连接 / input tap / swipe / keyevent / screencap
│   │   ├── MuMuLocator.cs   # 定位 MuMuManager.exe，解析实例（ADB 端口 + 窗口句柄）
│   │   ├── EmulatorSession.cs # 会话：渲染窗口跟踪（自适应边框）、屏幕↔设备坐标映射、注入
│   │   └── EmuShotDialog.cs # 截图取点：点画面生成 device 坐标事件
│   ├── GlobalHook.cs        # 全局键盘/鼠标低级钩子（WH_*_LL）
│   ├── Simulator.cs         # SendInput 输入模拟
│   ├── KeyMap.cs            # 虚拟键码 <-> 可读名称
│   ├── MacroEvent.cs        # 事件模型
│   ├── MacroStore.cs        # 宏 JSON 保存/加载、设置持久化
│   └── Win32.cs             # Win32 P/Invoke 声明
└── MacroClicker.Mobile/     # Android 手机端（Kotlin）
    ├── build.gradle.kts     # AGP 8.7 / Kotlin 2.0 / minSdk 26
    └── app/src/main/
        ├── AndroidManifest.xml
        ├── java/com/macroclicker/mobile/
        │   ├── model/MacroConfig.kt          # 事件/配置模型（兼容桌面端宏 JSON）
        │   ├── store/ConfigStore.kt          # 配置持久化 + 屏幕尺寸/坐标换算
        │   ├── service/ClickService.kt       # 无障碍服务：dispatchGesture 回放引擎
        │   ├── overlay/FloatingControls.kt   # 悬浮球 + 控制面板（可拖动/位置记忆）
        │   ├── overlay/PickOverlay.kt        # 全屏取点浮层（点哪取哪/标记可删）
        │   └── ui/                           # 主界面 / 列表适配器 / 编辑对话框
        └── res/                              # 布局与资源（dp/sp，深浅色跟随系统）
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

`delay` = 执行该事件前需等待的秒数。桌面端事件类型：`mouse_click` / `mouse_down` / `mouse_up` / `move` / `wheel` / `key` / `hotkey` / `wait`。

手机版使用同一结构，事件类型为 `tap` / `swipe` / `wait`，并附带 `screen`（保存时的屏幕分辨率，跨设备导入时自动按比例换算坐标）与 `settings`（执行模式）扩展字段；导入桌面端宏时会自动把 `mouse_click` 映射为 `tap`、`wait` 保持不变。

## 注意事项

- 点击使用**屏幕绝对坐标**，回放前请保持目标窗口位置与录制时一致（手机版跨设备导入时会自动按分辨率比例换算）
- 管理员权限窗口中的操作可能无法被录制/回放（钩子与 SendInput 权限限制），必要时以管理员身份运行本程序
- 部分游戏使用驱动级输入或反作弊系统，可能拦截模拟输入（手机端同理，部分 App 会屏蔽无障碍手势）
- 请勿将本工具用于违反目标平台规则的场景
