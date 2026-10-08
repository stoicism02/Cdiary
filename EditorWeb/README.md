# 小岛文档编辑器

本地 WPF / WebView2 宿主加载此目录，通过 `island-editor.local` 的资源请求处理器提供本地资源，不启动 HTTP 服务。程序资源按文件清单匹配，`/attachments/` 仅接受附件 ID 路径并返回缩小的预览图；外部请求被阻止。主窗口使用 `DisplayOnly` 原生预览，不创建 WebView2；独立便签使用 `WritingOnly`，打开即加载文档编辑，关闭、隐藏或切任务时保存并释放。

## 阅读与编辑交接

便签进入编辑时，原生预览保留在背后，WebView2 隐藏准备；`contentReady` 确认当前会话内容已排版，`presentationReady` 确认位置恢复及可见图片准备完成后才交接。图片等待最多 600 ms，损坏图片不会无限阻塞。

`writingOnly` 隐藏“源码”和“完成”，焦点离开正文或浏览器窗口时发送 `saveNow`，Ctrl+S 也只执行补存；宿主执行 `FlushAsync`，保存后仍可编辑。组词期间推迟到 `compositionend` 再补存。失败保留正文并显示状态提示，原有 500 ms 自动保存持续有效。旧 `sticky/saveReadOnly` 协议保留供控件兼容测试，生产便签不再启用。外部内容更新会清除旧编辑器撤销记录，避免撤销覆盖外部新内容。

原生与 DOM 使用归一化段落文本、重复序号、段落索引与段内比例交接阅读位置，文本变更时退回索引或总体比例。两种排版引擎不保证逐像素相同。字号统一 13，常规工具区 44、底栏 25；较矮区域工具区 34、底栏 20。只读正文维持正常对比度。

## 视觉约定

- 纸色 `#FFFBEA`，正文 `#725D42`，标题 `#794F27`，强调 `#19C8B9`，选区工具底色 `#DDF7F1`，分隔 `#E5DCC7`。
- 正文 Microsoft YaHei UI 13 px，代码 Consolas。左对齐，正文宽度与原生阅读区一致。
- 一条紧凑工具栏、连续纸面和按需出现的选区工具。小窗口折叠次要按钮，保留待办入口，不整体缩放文字。

## 固定依赖

- Vditor **4.0.0**，MIT，来源 `https://registry.npmjs.org/vditor/-/vditor-4.0.0.tgz`。
- 下载包 SHA-256：`D88108B759C744648C950EED55C91D845805EF80CA82A2BA6628AA0BC2D7D1ED`。
- 仅保留 `dist/index.min.js`、`dist/index.css`、Lute、简体中文、Ant 图标、light 内容主题与 LICENSE，合计约 4 MB。
- WebView2 SDK **1.0.4258.31**，使用电脑已有 Evergreen Runtime；没有运行时或资源损坏时回退原生源码编辑。

升级 Vditor 时必须重新验证本目录使用的 DOM 选择器及 `vditor.wysiwyg`、`afterRenderTimeoutId` 等版本相关接入点。所有生产资源随程序复制；不使用在线 CDN、远程渲染或上传接口。

## 数据协议

宿主下发 taskId、随机会话 token、note revision、原文。Vditor 读取后的规范化文本只作为比较基线，不在加载时回写数据库。真实输入提交 baseText / revision 与新原文；宿主匹配任务与版本，更新模型并在 500 ms 后保存。结束编辑与退出会先等待浏览器提交最后输入。

最近会话身份保留 32 份，保证切换任务前排队的输入仍写到原任务。过期版本不覆盖新笔记，而是存入 `note_recovery`（每任务最近 5 份）；首次编辑原文存入 `note_originals`。两个副本入口在“更多 → 备份 / 找回”，可导出 Markdown。删除任务同时删除这些副本。浏览器缓存关闭，SQLite 是唯一内容存储。

支持标准 Markdown / GFM 的基础内容。公式、图表暂走源码模式；图表库、录音与协作功能不打包。

PNG / JPEG 图片支持粘贴、文件拖入、“更多”或斜杠菜单选择。导入限制为单图 20 MB、4000 万像素，SHA-256 去重，原图与 1024 像素预览独立保存于日记数据目录下的 `Attachments`。Markdown 使用 `attachments/<sha256>.<png|jpg>`，浏览器仅在导入传输时使用 Base64，不将 Base64 写入正文。双击请求原生原图查看器。

导入请求携带会话 token 和 request ID，浏览器保留 Range 与版本，宿主再次校验任务和版本；编辑位置变化时取消插入。导入成功后调用原有 Markdown 插入及自动保存通道，撤销/重做保留附件。清理仅由设置页确认触发，扫描笔记、原文备份、冲突副本等引用，并保留本次进程中使用过的图片（保守覆盖两种编辑器的撤销记录）。取消插入的已保存附件在后续清理时回收。

完成编辑会释放浏览器，文档模式的撤销历史在此时结束；源码模式仍保留原有最近 20 份编辑状态。再次进入文档模式从已保存 Markdown 建立新会话。

快捷键在正文获得焦点时生效：Ctrl+B/I/K 对应加粗、斜体、链接，Ctrl+1～6 设置当前段落标题，Ctrl+0 恢复正文，Ctrl+Z/Y 撤销和重做。复用 Vditor 格式命令，并明确标记修改以同步模型和数据库。标题转换跳过代码块和表格，链接输入框及中文组词期间保留原始按键行为。

## 章节目录

工具栏“目录”发送 `outlineToggle`，原生共享面板负责显示。宿主通过 `outlineEnabled` 启停追踪；仅展开且可见时提取实际 h1–h6 节点，排除代码区。编辑变更合并 300 ms 后刷新，输入法组词期间暂停；滚动通过事件与单次 requestAnimationFrame 更新当前章节，不使用持续定时轮询。

目录按钮再次点击会收起，`aria-expanded` 与宿主状态同步。窄浮层不使用 Popup 的鼠标捕获自动关闭，避免同一次点击先触发关闭再重新展开；原生窗口处理外部点击，WebView2 通过 `outlineDismiss` 转发正文点击和 Esc。宽侧栏不因正文点击而关闭。

`outline` / `outlineActive` 消息包含会话 token 和目录 version，宿主同时检查当前编辑器与任务。标题节点用 WeakMap 分配身份，同名标题独立定位，不将目录标记写进 Markdown。`outlineJump` 校验会话及目录版本后滚动到节点并短暂高亮，不修改正文或选区。窄窗口原生浮层导航后关闭，宽窗口停靠右侧。正文实际滚动容器是 `vditor.wysiwyg.element`（PRE），不是外层 `.vditor-wysiwyg`。

自定义目录按钮使用 `note-outline` 名称；`outline` 是 Vditor 的内置按钮名，会忽略自定义 click 回调，不能复用。

## 验证

`dotnet run --project tests\DocumentEditor\DocumentEditor.csproj -c Release -- preview-document`

在不可见 HWND 中运行实际 WebView2，使用临时数据库，验证 Markdown 快捷输入、列表续写、复选框、选区工具、斜杠菜单、撤销、保存、备份、冲突保留、双窗口同步、窄窗口、按需创建与释放。中文组词的事件流程可自动验证，但不同中文输入法的候选窗、拖选与触控板手感仍需要真实设备手动验收。

2026-10-04 的本机样板：单个编辑器连测试宿主的 Private Bytes 约 310–320 MB，双编辑器约 365–380 MB；初始化约 0.9–1.7 秒。它们不是相对正式版的增量，也不是其他电脑的保证值。短时 CPU 采样受初始化和渲染影响，不能当成稳定空闲占用。用户已选择默认轻量预览、需要输入时再加载。
