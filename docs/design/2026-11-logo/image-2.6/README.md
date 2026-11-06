# Image 2.6 暖石墨 Logo 提案

三款透明原稿分别通过图像生成 API 显式请求 `gpt-image-2.6-sunburst` 获得。下表保留生成时的 1024 像素原稿、请求参数和原稿 SHA-256，可用于核对来源或在选定后继续细化。

| 方案 | 三种使用场景 | 导出 | 源稿与生成记录 |
|---|---|---|---|
| A · 折带 | [标题栏、任务栏与 Banner](fold-proof.png) | [512 像素标记](fold-mark.png) · [完整横幅](fold-banner.png) | [原稿](originals/fold.png) · [参数与校验值](originals/fold-receipt.json) |
| B · 双层档案夹 | [标题栏、任务栏与 Banner](drawer-proof.png) | [512 像素标记](drawer-mark.png) · [完整横幅](drawer-banner.png) | [原稿](originals/drawer.png) · [参数与校验值](originals/drawer-receipt.json) |
| C · 拉链印 | [标题栏、任务栏与 Banner](clasp-proof.png) | [512 像素标记](clasp-mark.png) · [完整横幅](clasp-banner.png) | [原稿](originals/clasp.png) · [参数与校验值](originals/clasp-receipt.json) |

每张场景图的上排来自真实 WPF 标题栏控件，候选图占 22 DIP。中排左侧是独立测试窗口挂上候选 ICO 后，从本机 Windows 11 Explorer 任务栏截取的按钮。右侧按原像素展示同一 ICO 的 16、24 和 32 像素图层。下排采用 1280 × 480 的 GitHub 仓库横幅，三版使用相同的字标尺寸及版面。可按原尺寸打开图像对比。
