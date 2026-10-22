# 桌面待办卡片

> 状态：已发布（2026-10-22）｜GitHub: [kreiitor/desktop-todo-card](https://github.com/kreiitor/desktop-todo-card)

一个常驻 Windows 桌面的轻量待办小卡片。没有标题栏和任务栏按钮，默认贴在桌面层级；点右上角添加，点左侧圆圈完成，支持截止时间和周期任务。

- 单文件 C# WinForms，使用 Windows 自带的 .NET Framework 4.x 编译和运行，不需要 Electron 或 Python 运行时。
- `TodoWidget.exe` 约 38KB；实测常驻 Working Set 约 35–47MB，Private 约 25MB。
- 任务保存在 exe 同目录的 `todo.json`，可以直接外部编辑，挂件每 2 秒自动刷新。
- 支持每天/每周/每月/每年重复、截止前 10 分钟托盘提醒、开机启动、透明度 80–100%。

## 基于上游二次开发

本项目基于 [panan3562-svg/desktop-todo-widget](https://github.com/panan3562-svg/desktop-todo-widget)（作者 **panan**，MIT 许可证）进行二次开发，**不是上游官方版本**。

上游提供了无边框圆角卡片、拖动、系统托盘、JSON 存储、外部编辑刷新和 DPI 感知等基础。本仓库在此基础上增加了：

- 截止时间（日期 + 时间）与每天/每周/每月/每年重复；
- 周期任务完成后自动顺延；
- 按截止时间排序、逾期/今天/周期任务分色；
- 截止前 10 分钟托盘提醒；
- 浅色纸感主题、无标题栏/底部栏、圆点勾选；
- 滚轮滚动、屏幕内高度限制；
- 置顶/贴桌面、透明度 80–100%、开机启动；
- 修复外部编辑后重复重载的问题。

上游版权声明保留在 [LICENSE](LICENSE) 中，具体改动见 [NOTICE.md](NOTICE.md)。

## 下载与运行

- 直接下载 [TodoWidget.exe](TodoWidget.exe)，或从 [Releases](https://github.com/kreiitor/desktop-todo-card/releases/latest) 下载。
- 放到一个有写权限的文件夹再运行（例如“文档”下的任意目录），会在同目录生成 `todo.json` 和 `widget_conf.json`。
- 需要 Windows 10/11，系统自带 .NET Framework 4.x 即可，无需安装运行时。
- 程序没有代码签名，Windows SmartScreen 可能提示“未知发布者”，选择“仍要运行”即可。
- 不要放在 `Program Files` 这类只读目录，否则无法保存任务。

## 使用

- **添加**：点右上角的橙色 `+`。
- **编辑**：左键点任务文字。
- **完成**：点任务左侧的圆圈；周期任务会顺延到下一次。
- **更多**：右键任务可编辑/删除/完成；右键空白处或日期栏打开应用菜单。
- **移动**：按住顶部日期区域拖动。
- **贴桌面 / 置顶**：默认不置顶，失焦后沉到桌面层；托盘菜单勾选「置顶显示」可常驻最前。
- **透明度**：托盘菜单 →「透明度」，可选 80/88/94/98/100%。
- **开机启动**：托盘菜单 →「开机自动启动」。
- **提醒**：截止前 10 分钟弹一次托盘气泡；超过 12 小时的旧任务不会补弹。

## 目录

| 路径 | 内容 |
| --- | --- |
| `TodoWidget.cs` | 主程序源码 |
| `TodoWidget.exe` | 编译产物，直接运行 |
| `build.bat` | 用系统自带 .NET Framework 编译 |
| `app.manifest` | DPI 感知清单 |
| `tray.ico` | 程序/托盘图标 |
| `todo.example.json` | 数据格式示例 |
| `LICENSE` / `NOTICE.md` | MIT 许可证与上游说明 |

## 数据格式

```json
{
  "updated": "2026-10-22T18:00:00",
  "items": [
    {
      "id": "t1",
      "text": "每周组会",
      "detail": "线上",
      "done": false,
      "due": "2026-10-25 18:00",
      "repeat": "weekly"
    }
  ]
}
```

- `due`：`yyyy-MM-dd HH:mm`，空字符串表示没有截止时间。
- `repeat`：`""` / `daily` / `weekly` / `monthly` / `yearly`。
- 周期任务的下一次时间由 `due` 推算，例如 `weekly` 就是每周同一天的同一时刻。
- 示例见 [todo.example.json](todo.example.json)。

## 构建

```bat
build.bat
```

需要 Windows 自带的 .NET Framework 4.x（`csc.exe`），不需要 Visual Studio 或 .NET SDK。源码、`tray.ico`、`app.manifest` 和 `build.bat` 放在同一目录即可。

## 已知限制

- 只有浅色主题；深色主题还没做。
- 提醒固定提前 10 分钟，暂不可在界面里配置。
- 重复规则只支持固定间隔，不支持“每月最后一个周五”这类复杂规则。
- 没有云同步/多设备；数据只在本机 exe 同目录。
- 任务很多时是滚轮滚动，没有可见滚动条。
- 仅支持 Windows 10/11。

## 许可证

MIT。上游版权归 **panan**，本仓库修改部分归 **kreiitor**；全文见 [LICENSE](LICENSE)。
