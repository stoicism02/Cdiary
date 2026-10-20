# 本轮验证与限制

日期：2026-10-13。当前软件基线：`e96f50e`。本轮所有可交付改动均位于 `docs/ui-redesign/`。

**后续范围修订：用户要求保留现有任务便签。** 评审台已改为展示现状便签截图；以下 105 个断言与三张便签原型图记录初稿验证，不能作为修订后便签设计依据。便签重设计已取消，生产便签未改动。

## 当前软件取证

使用仓库已有的独立测试 / 预览宿主，加载示例数据并实例化实际 WPF 控件。没有启动第二个正式实例，也没有操作正在运行的软件、个人数据库或剪贴板。

| 执行项 | 结果 | 用途 |
| --- | --- | --- |
| `tests/Preview`，Release | 成功输出预览图 | 主窗口、月历、搜索、完成、当日待办的实际布局 |
| `tests/FocusUI`，Release | 32 项检查通过 | 实际专注控件与宽窄布局 |
| `tests/Screenshots`，Release | 28 项检查通过 | 实际截图设置、缩略图列表；使用生成的样例图片 |
| `tests/NoteAppearance`，Release | 28 项检查通过 | 实际透明便签与 WebView2 编辑器，离屏窗口 |

13 张选用图片及原始文件来源保存在 [manifest.json](evidence/manifest.json)。已人工查看标准 / 宽 / 窄主页、窄月历、专注、截图设置与图库、真实便签编辑器。

主程序为 WPF，没有可供 Playwright 访问的网页地址。浏览器查看的是上述图片组成的证据画廊，不能代替原生窗口的交互测试。主窗口预览宿主没有注入专注服务，因而主窗口截图中缺少专注入口；这不是正式版本缺失功能。便签图中的“保存中”是采集测试输入后的瞬时状态。

## 设计原型验证

使用本机 Chrome 的无头模式及 Playwright，检查三套 HTML 方案。结果保存于 [verification-results.json](concepts/verification-results.json)。

- 三方向 × 七页面 × 两个浏览器宽度（1440、760 CSS px）：页面能渲染，没有整个网页的横向溢出。
- 每个方向检查背景不透明度改变、正文不透明度保持 1、失焦保存到页面内存、再次打开可读回示例文字。
- 检查完成移出清单、完成归档、撤销恢复、今日收起 / 继续、搜索过滤、自由计时不显示目标进度。
- 13 张当前控件截图可在浏览器完整解码；无 JavaScript 页面异常。
- 共 105 个断言通过。它们是本轮评审原型的检查，**不表示生产 UI 已重构或通过验收**。

输出 9 张原型参考图：每案宽 / 窄首页与便签。已人工检查三个宽屏方向、窄屏清单和便签的视觉层次。图片上方均标明“方案原型，未实施”。

| A · 轻岛 | B · 纸页 | C · 专注台 |
| --- | --- | --- |
| [宽屏](concepts/a-1440-prototype.png) / [窄屏](concepts/a-760-prototype.png) / [便签](concepts/a-sticky-prototype.png) | [宽屏](concepts/b-1440-prototype.png) / [窄屏](concepts/b-760-prototype.png) / [便签](concepts/b-sticky-prototype.png) | [宽屏](concepts/c-1440-prototype.png) / [窄屏](concepts/c-760-prototype.png) / [便签](concepts/c-sticky-prototype.png) |

## 复现与使用

直接用浏览器打开 `index.html` 即可，不依赖网络。需要在浏览器面板中预览时，可运行：

```powershell
node docs/ui-redesign/preview-server.cjs
```

默认仅监听本机 `127.0.0.1:8765`，仅提供本目录静态文件。浏览器访问 `http://127.0.0.1:8765/`。

重新验证和截图：

```powershell
node docs/ui-redesign/verify-prototype.cjs
```

脚本默认使用本机 Codex 缓存的 Playwright 与 Chrome；其他机器可设置 `PLAYWRIGHT_MODULE` 和 `CHROME_PATH`。原生取证应运行对应测试项目，不应运行这份 HTML 验证脚本来声称原生功能通过。

## 明确未验证的内容

- HTML 原型是结构、样式与部分状态演示。日期切换仍共用样例任务；计时数字为固定状态；图片原图是占位示意；置顶没有调用系统 API；设置不会落到真实配置。
- 浏览器原型使用 1050 px 高度和完整页面截图，窄图验证的是宽度适配，不能证明将来原生 760 × 580 窗口的可见行数。相关目标须在选案后实机确认。
- 未验证重设计后的 WPF 渲染、真实 Markdown / IME、屏幕阅读器、多显示器、系统快捷键、所有高 DPI 比例。
- 未测量本轮设计对 CPU、内存、启动速度的影响。原型不引入新的生产框架；性能收益不能由截图推断。
- 便签降低背景不透明度后，复杂桌面与图片可能降低可读性。方案中保留可调节的不透明底色；真实组合仍需实机测试。

本轮到设计选型为止，没有更新 `dist/current`，没有提交或推送 Git。
