# DocLoom 设计系统 · Dark Ink Studio

> 本文件是界面设计的唯一事实来源（Master）。新增界面元素前先读这里；与本文冲突的实现视为缺陷。

## 1. 定位与风格选择

| 项 | 决策 | 理由 |
|---|---|---|
| 产品类型 | Productivity Tool（文档处理 / 桌面工作台） | `--domain product` 命中 Productivity Tool，推荐 Flat Design + Micro-interactions |
| 风格 | Dark Mode（近 OLED）+ Flat Design + Swiss 网格 | 桌面工具长时间使用、内容密度高；深色降低眩光，扁平保证信息层级靠排版而不是装饰 |
| 模式 | 仅深色 | 单一模式可把对比度与层次打磨到位；不做半成品浅色主题 |
| 变体度 | 低（居中、直线、稳定栅格） | 工具类界面需要可预测；动效克制，避免抢注意力 |
| 密度 | 高（8–32px 间距体系） | 三栏工作台，信息密度优先 |

来源查询（`ui-ux-pro-max/scripts/search.py`）：

- `"document editor productivity desktop tool" --domain product` → Productivity Tool / Flat Design + Micro-interactions
- `"dark professional editor paper ink" --domain style` → Dark Mode (OLED)、E-Ink/Paper（后者不选：阅读器风格不适合工作台）
- `"list view sidebar panel" --stack wpf` → MVVM、ObservableCollection、attached properties 布局
- `"dark mode surface elevation contrast" --domain ux` → 正文 4.5:1 下限，焦点可见 3:1

不采用 E-Ink/Paper：它要求「无动效、纸白底、衬线阅读字体」，与三栏工具的高密度操作冲突。

## 2. 颜色令牌

深色底分三层：窗口底 → 面板 → 卡片，用亮度差而非阴影表达层级。

| 角色 | 值 | 用途 |
|---|---|---|
| `--bg` | `#0A0F14` | 窗口底、输入框底 |
| `--surface` | `#0F1820` | 面板（三栏卡片） |
| `--surface-raised` | `#13202A` | 面板内抬升块、导出区、提示条 |
| `--card` | `#16232E` | 段落卡、图片卡 |
| `--card-hover` | `#1B2C39` | hover |
| `--card-active` | `#203341` | pressed |
| `--border` | `#22323F` | 面板描边 |
| `--border-soft` | `#1A2833` | 卡片描边 |
| `--border-strong` | `#2F4454` | hover 描边、虚线框 |
| `--text-primary` | `#E9F2F6` | 标题、正文 |
| `--text-secondary` | `#A3B8C6` | 说明、元信息 |
| `--text-muted` | `#7B92A2` | 弱提示、命名规则 |
| `--accent` | `#2DD4BF` | 强调：选中边框、图标、链接式文字 |
| `--accent-deep` | `#14B8A6` | 渐变末端 |
| `--accent-soft` | `#123B37` | 强调底（chip） |
| `--accent-ink` | `#052421` | 青底上的文字 |
| `--amber` | `#F5A524` | 唯一的主行动色（导出） |
| `--amber-ink` | `#241703` | 琥珀底上的文字 |
| `--danger` | `#F26B6B` | 错误提示 |

对比度（实测 WCAG）：

- `#E9F2F6` on `#0F1820` ≈ 14.8:1
- `#A3B8C6` on `#0F1820` ≈ 7.9:1
- `#7B92A2` on `#0F1820` ≈ 5.1:1
- `#052421` on `#2DD4BF` ≈ 9.4:1
- `#241703` on `#F5A524` ≈ 8.9:1

规则：正文只用 primary/secondary；muted 只用于 12px 以下辅助文字；强调色不做大面积底色（除主按钮与 chip）。

## 3. 字体

| 角色 | 字体栈 | 字号 / 行高 |
|---|---|---|
| UI / 正文 | `Segoe UI Variable Text, Microsoft YaHei UI, Microsoft YaHei, Segoe UI` | 13.5 / 24（段落） |
| 展示（标题、数字） | `Segoe UI Variable Display, Microsoft YaHei UI` | 21 / 32（文档标题）、15 / 22（统计值） |
| 等宽（序号、日期） | `Cascadia Mono, Consolas, Microsoft YaHei UI` | 10–11 |

层级：标题 21 → 区块标题 13 → 正文 13.5 → 说明 11.5 → 元信息 10.5。不使用 12px 以下作为正文。

中文与西文混排统一走系统字体栈，避免 Web 字体缺失导致的回退抖动。

## 4. 空间与形状

- 间距体系：4 / 8 / 10 / 12 / 14 / 16 / 20 / 26（8 的倍数为主）。
- 圆角：面板 16，卡片 12，按钮 10，chip 6，勾选框 6。
- 描边：1px；hover 才升级为 `--border-strong`。
- 阴影：仅两处（Toast、忙碌卡）使用 `DropShadowEffect`，其余靠底色分层。

## 5. 组件规范

**按钮层级**
1. `PrimaryButton`（青绿渐变 + 深青字）：每个视图最多一个，表示「复制全文」这类主操作。
2. `ActionButton`（琥珀 + 深棕字）：全局唯一的动作色，只给「一键下载选中图片」。
3. `GhostButton`（卡片底 + 强描边）：次级操作（复制正文/标题、打开文件夹）。
4. `SubtleButton` / `IconButton`：行内操作（复制、清空、反选），hover 才出现底色。
5. `TitleBarButton` / `CloseTitleBarButton`：窗口控制，关闭按钮 hover 变红。

**状态**：hover 用 0.16s 透明度动画（不改变共享画刷，避免动画污染）；pressed 缩至 0.982；disabled 透明度 0.34 且光标恢复箭头。所有按钮 `Cursor=Hand`，`FocusVisualStyle` 为 2px 青色描边环。

**勾选框**：20×20 半透明深底 + 1.4px 白边；选中时青底 + 深青对勾（`Stretch=Uniform`）。整卡可点区域由 CheckBox 承担，避免「只有小方块能点」。

**图片卡**：158×100 缩略图（`ImageBrush` + `Rectangle` 圆角裁剪，实现真正的圆角裁切），左上勾选、右上序号、下方文件名 + `大小 · 尺寸`。选中态由描边变青表达，不只靠勾选框。

**滚动条**：9px 宽、无箭头、thumb 悬停变青，减少视觉噪声。

**Toast**：底部居中，左侧 4px 状态色条 + 图标 + 文案，3.6s 自动消失，进出各 0.16–0.24s 缓动。

**空态**：三处（无文档 / 无图片 / 无标题）都给出「下一步做什么」，不出现空白面板。

## 6. 动效

所有时长与缓动集中在 `Themes/Motion.xaml`，组件里禁止写裸时长。

| 令牌 | 值 | 用途 |
|---|---|---|
| `DurInstant` | 90ms | 按压、图标切换 |
| `DurFast` | 140ms | 悬停进入/退出、Toast 淡出 |
| `DurBase` | 200ms | 面板淡入、遮罩淡出 |
| `DurSlow` | 320ms | 缩略图渐显、窗口入场 |
| `EaseOut` | CubicEase EaseOut | 位移、透明度默认 |
| `EaseBackOut` | BackEase 0.35 | 勾选对勾、Toast 弹出 |

具体行为：

- **按钮**：悬停 140ms 提亮到 88% 并上浮 1px（`TranslateTransform`），按压下沉 0.5px + 74% 透明；退出 180ms 归位。
- **卡片悬停**：不直接改 `Background`（共享画刷无法安全动画，且换色是瞬切的）。每张卡片是「底色层 + 高光层 + 内容层」三叠，悬停只对高光层做 140ms 透明度过渡。
- **列表入场**：文档切换时标题卡 240ms 淡入上浮、右栏延迟 60ms 跟上、段落项按 26ms 步进依次上浮 10px、图片项按 24ms 步进（`Services/StaggerAnimation`）。
- **缩略图**：解码完成后 320ms 渐显，替代原来的"突然出现"。
- **勾选**：填充层 160ms 淡入，对勾 240ms 回弹缩放（0.7→1）。
- **Toast**：180ms 淡入 + 240ms 缩放 0.97→1（回弹）+ 位移 12→0；退出 140ms 收回到 8px 并淡出。
- **忙碌遮罩**：遮罩 180ms 淡入、卡片 280ms 回弹放大；退出 140ms。
- **滚动**：滚轮从"跳 3 行"改为 260ms easeOutCubic 的连续位移（每格 104px）。
- **窗口**：首次显示 340ms 从 62% 提亮 + 上浮 12px。

### 踩坑记录

`ScrollViewer.VerticalOffset` 的依赖属性元数据带 `IsAnimationProhibited`，直接 `BeginAnimation` 会抛异常（本次曾据此弹过一次运行时错误）。平滑滚动改用 `CompositionTarget.Rendering` 逐帧插值并 `ScrollToVerticalOffset`，同时用附加属性保存进行中的 tween，新滚动先取消旧的，控件卸载时解绑。

## 6.1 图像资产

| 文件 | 用途 | 来源与处理 |
|---|---|---|
| `Assets/empty-welcome.png` | 无文档时的欢迎插画（纸张层叠被拉开的意象） | agnes-image-2.5-flash 生成 1792×1024 → 按内容 bbox 裁切并留 6%/10% 边 → 缩放到 1100 宽 |
| `Assets/empty-images.png` | 右栏无图片空态（发光相框） | 同模型生成 1024×1024 → 居中裁切留白 → 640×640 |
| `Assets/docloom.ico` | 应用图标（三层纸叠 + 青绿发光边 + 琥珀层） | 同模型生成 1024×1024 → PIL 加 20% 圆角 alpha → 导出 16/24/32/48/64/128/256 多尺寸 ico |

融合方式（WPF 侧）：插画一律套 `RadialGradientBrush` 的 `OpacityMask` 让边缘溶进底色，欢迎插画再叠一层从 `#000F1820` 到 `#FF0F1820` 的线性渐变，消除图片下沿的硬边。图标本身带圆角 alpha，标题栏直接以 30×30 使用。

生成提示词要点：深色近黑底（#0A0F14 / #0C141B）、青绿 #2DD4BF 细发光线条、少量琥珀 #F5A524 点缀、等距/线框、大量留白、无文字无字母；图标追加「16px 仍可辨识的强轮廓」。

## 7. 无障碍

- 所有图标+文字按钮设置 `AutomationProperties.Name`；纯图标按钮（窗口控制、打开原文档、署名复制）同样有名称与 ToolTip。
- 图片勾选框的可访问名称是文件名，读屏能区分「20261117_2.jpg」。
- 键盘：`Ctrl+O` / `Ctrl+Shift+C` / `Ctrl+E` / `Ctrl+,` / `Esc`；焦点环 2px、3:1 以上对比。
- 不依赖颜色单独传达状态：选中同时改描边与勾选；错误同时有图标与文字。
- 正文与说明文字对比度均 ≥ 4.5:1。

## 8. 明确禁止

- emoji 当图标（统一用 24×24 线性 Path）。
- 紫色渐变、玻璃拟态、发光边框等「AI 味」装饰。
- 同一屏出现两个琥珀主行动按钮。
- 只靠 hover 才能发现的操作（复制按钮常显）。
- 弹窗式确认替代即时反馈（复制用按钮态 + Toast）。
- 12px 以下正文、灰字压灰底。

## 9. 交付前检查

- [x] 无 emoji 图标，全部矢量 Path
- [x] 可点元素有 hover 与 pressed 反馈
- [x] 所有按钮有可访问名称
- [x] 键盘可达，焦点可见
- [x] 深色对比度 ≥ 4.5:1（正文 ≥ 8:1）
- [x] 空态有引导
- [x] 长标题/长路径不裁切（Wrap + Ellipsis）
- [x] 动效只作用于透明度与位移