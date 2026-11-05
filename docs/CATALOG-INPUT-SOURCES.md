# 家具目录来源与多格式输入设计

核验日期：2026-09-08（America/Los_Angeles）。Revit 源码基线：179b386。

本文保存 2026-09-08 的官方来源研究与目标兼容范围。2026-09-10 已据此开发首版解析、产品复核和批次家具族流程；实际支持范围与尚未完成项见 [CATALOG-WORKFLOW.md](./CATALOG-WORKFLOW.md)。来源核验、文件摘要和人工预期样例见 [catalog-source-manifest.json](./catalog-source-manifest.json)，不能将来源清单直接当作全部格式已兼容的证明。

## 选定的官方来源

| 品牌 | 首选样本 | 内容及适用场景 |
| --- | --- | --- |
| Herman Miller | [OE1 多产品目录 PDF](https://assets.hermanmiller.com/content/dam/hermanmiller/documents/product_literature/brochures/oe1_workspace_collection_brochure.pdf)；[系列网页](https://www.hermanmiller.com/products/portfolios/oe1-workspace-collection/) | PDF 11 页，版权页为 2023。第 10 页有 19 个产品图块；第 3 页展示多产品场景。测试产品发现、区域裁切、跨页归组和网页补充规格。19 图块只是候选条目，不能直接视为 19 个完整 SKU。 |
| Herman Miller | [Aeron 规格 PDF](https://www.hermanmiller.com/content/dam/hermanmiller/documents/product_literature/product_sheets/aeron_chairs_product_sheet.pdf)；[手册 PDF](https://www.hermanmiller.com/content/dam/hermanmiller/documents/product_literature/brochures/aeron_chairs_brochure.pdf)；[规格网页](https://www.hermanmiller.com/products/seating/office-chairs/aeron-chair/specs/) | 规格 2 页、手册 12 页，均有 2026 内容。规格页混合 A/B/C 工作椅和两个高脚椅版本；网页进一步区分扶手配置。适合尺寸图关联、可调范围和跨资料核对。 |
| Herman Miller | [Wireframe Sofa 网页](https://store.hermanmiller.com/living-room-furniture-sofas/wireframe-sofa/2195905.html?lang=en_US) | 图片、材质、当前 SKU 和双人/三人尺寸在同一页；含分数英寸。适合测试一个网页包含多个规格，以及图片、SKU、尺寸的配置绑定。 |
| Steelcase | [Gesture 规格 PDF](https://www.steelcase.com/resources/documents/gesture-brochure)；[Wood Seating 多产品目录 PDF](https://www.steelcase.com/content/uploads/2014/12/woodseating1.pdf) | Gesture 3 页，2025 年 2 月；第 2 页列五种型号，第 3 页有尺寸和材料。URL 没有 PDF 扩展名。Wood Seating 84 页、2015 年 4 月，适合非相邻说明/订货页关联；仅作历史结构样本，不代表当前供货或价格。 |
| Knoll | [k. lounge 产品页](https://www.knoll.com/product/k-lounge)；[合集 PDF](https://library.hermanmiller.group/asset/366507bc-22b6-43cc-83cd-4b3d7bb61590/klounge_collection.pdf)；[Saarinen 规格 PDF](https://library.hermanmiller.group/asset/a15f5618-ae2d-41f6-8d5a-94eedfa7b0d9/saarinen_pedestal_collection_product_sheet.pdf) | 两份 PDF 均 2 页。k. lounge PDF 为 2016 年、描述 11 模块；当前网页描述 20 模块。Saarinen 未确认文档年份，包含多种桌型及尺寸范围。测试版本差异、型号归属、系列尺寸不能任意组合的问题。 |
| Muuto | [Fiber Armchair Tube Base 产品页](https://www.muuto.com/product/Fiber-Armchair-Tube-Base)；[官方资料库](https://download.muuto.com/digitalshowroom/) | HTML 有明确 SKU、厘米/英寸尺寸、尺寸图与下载链接。测试结构化产品页，以及同系列木底座、管状底座、旋转底座和软包版本的区分。资料库本次仅读到动态入口，具体文件和登录要求未验证。 |
| HAY | [AAC22 具体配置页](https://www.hay.com/hay/furniture/seating/chair/about-a-chair/aac22-lacquered-oak-4-leg-base-standard-glider-black-20-shell-none-none)；[目录入口](https://www.hay.com/footer/catalogues) | HTML 有具体 SKU 和尺寸字符串，但本次所读字段未明确单位。目录入口包含 HAY Newspaper 2026 等 Issuu 翻页目录；已核实入口，未核实翻页内容及下载条件。 |
| Vitra | [Allstar 产品页](https://www.vitra.com/en-us/product/allstar)；[英文 Factbook](https://static.vitra.com/media/asset/854608/storage/master/download/Factbook%2520Allstar-EN.pdff) | 产品网页可读；Factbook 返回 PDF 文本、14 页、EN 2025。后续截图和直接访问存在 403/404，稳定下载及视觉检查未完成。设计年份 2014 应与文档年份分开。 |

来源清单覆盖 6 个品牌、20 个具体入口。官网所列下载资源与成功取得并验证的文件在清单中分别标记。

## 已完成的样本检查

- 下载三份 Herman Miller PDF，共 25 页、约 14.9 MB；验证 PDF 文件标识、页数、逐页文字提取和 SHA-256。原文件作为临时研究样本保存，未纳入仓库。
- 视觉抽查 OE1 第 3、10 页和 Aeron 规格第 2 页。确认文字提取不能独立恢复产品与尺寸图的关联。
- OE1 场景照片包含多件家具、人物和装饰；一幅图可以关联多个产品，但生成输入应是经过确认的单产品区域。
- Aeron 手册存在无可提取文字的页面。应保留图像页，不可把“没有文字”直接判定为无效页或扫描件。
- Wireframe 网页的尺寸区域同时包含双人、三人规格；当前选中的 SKU 及图片不应自动套用到两个规格。
- Muuto 同时提供厘米值和经过舍入的英寸值。应保留原始两套数值及来源，不能把换算后的轻微差异当作实质冲突。
- Knoll 两份 PDF 由并行审核实际读到文本；主审核再次读取二进制链接时出现间歇性错误。产品网页已再次打开核实。
- 失效、403、动态空壳与成功读取均保留不同状态；未据此推断是否必须登录。

## 输入兼容范围

所有输入先形成统一产品资料包，再进入现有 Tripo 和 Revit 流程。每个适配器的支持状态应独立展示，不能宣称通用网址均已兼容。

| 优先级 | 输入 | 处理要求 |
| --- | --- | --- |
| 第一阶段 | 本地 PDF、公开 PDF 链接 | 按实际内容识别类型；读取文字、坐标、图片和图形；必要时渲染/OCR；同时保留物理页码与印刷页码。支持多栏、跨页、续表和图片页。 |
| 第一阶段 | 单个产品网页 | 提取主体、规格表、图片和产品身份；使用页面实际提供的结构化数据作为辅助并与可见内容核对；保存区域、语言和配置状态。 |
| 第一阶段 | 系列页、产品列表或多个 URL | 先列出产品候选，再沿明确关联的详情、尺寸、图片和资料链接扩展。限定品牌范围和页面数量，提供进度及遗漏提示。 |
| 第一阶段 | PNG/JPEG/WebP、截图、图片目录 | 接受多图和补充描述；识别尺寸图与产品照片；保存原图及裁切区域，不凭图片数量推断标准多视图。 |
| 第一阶段 | 混合文件夹或 ZIP | 把 PDF、图片和已支持的说明文件关联为一个资料批次；限制文件数量/解压体积，拒绝路径越界；不执行包内文件。 |
| 后续适配 | 在线翻页目录 | 优先使用网站提供的公开 PDF 或导出；否则通过受支持的可见页面读取保留页序。需登录、不可读取或受限制时明确提示，不承诺全平台自动抓取。 |
| 后续适配 | CSV/XLSX 产品清单 | 支持列映射与图片关联，保留型号前导零、原始单位和来源行；不能将所有尺寸/颜色列做无依据的全组合。 |
| 独立模型路线 | 官方 RFA/CAD/3D 下载 | 可作为对照或可选直接导入来源。需要验证具体型号、格式、依赖、版本和宿主加载；这与目录解析和 Tripo 图像生成是不同适配器。 |

上表是目标兼容范围；首版只实现其中有明确标注的子集。OCR、完整页面渲染、ZIP、表格和翻页目录等仍是后续工作，实际状态以 [CATALOG-WORKFLOW.md](./CATALOG-WORKFLOW.md) 为准。

## 统一产品资料包

一个产品可跨多个文件/网页；一个页面也可包含多个产品。建议保留以下信息：

- 身份：品牌、产品系列、型号、SKU、具体规格；区分制造商品牌和销售网站，不以域名直接断定品牌。
- 来源：原始 URL/文件摘要、取得时间、文档标注年份、地区和语言；每个字段对应页码/图块坐标或网页区域。
- 图片：原图、内容摘要、裁切区域、图片角色、视角以及所属配置。白底照、场景照、材料小样、尺寸线图各有不同用途。
- 文字：原始描述及用于产品整理的结构化字段，保留人工改正记录。
- 尺寸：原始文本、字段语义、单位、单值/范围、姿态及规格。整体深度、座深、整体高度、座高、直径和包装尺寸必须分开。
- 关联与核对：每个字段的候选来源、冲突、缺失和确认状态。不同年份/地区/配置尚未对齐时，不能只取最新日期覆盖旧值。
- 模型资源：仅列出的资源、成功下载的文件和通过宿主验收的资产分层记录。
- 输出：稳定产品标识、族名/类型名、生成任务、导入记录和摆放顺序。

尺寸缺失时保持未知。范围没有选定姿态时，暂不形成唯一生成目标。描述和来源数据留在本地目录资料存储中，不进入现有不保存提示词/原始路径的付费恢复日志。

## 首轮验收样本

1. **Muuto Fiber 管状底座椅**：用明确 SKU 和固定厘米尺寸验证 HTML 到产品资料包；图库中的其他颜色及底座不得串入当前规格。
2. **Wireframe 双人/三人沙发**：同页形成两个规格，分数英寸正确转换；在无明确关联证据时不复制当前 SKU。
3. **OE1 Storage Trolley、Rectangular Table、Project Table**：目录图块与跨页场景先归组，再从官方产品详情补充规格；缺失的具体配置仍须复核。
4. **Steelcase Gesture**：从第 2 页产品代码关联第 3 页尺寸类别；保留高度、宽度等可调范围。
5. **Knoll k. lounge**：先测试 KL1，再扩展相似模块；保留旧 PDF 和当前网页的版本差异。
6. **Aeron**：测试 A/B/C 与扶手、工作椅/高脚椅的层级，检查尺寸语义及资料差异。
7. **HAY/Vitra**：作为单位缺失、动态目录、异常扩展名和访问不完整的测试项；阻塞项目应给出明确原因。

清单文件里的数值样例由人工核对，用作未来解析器预期结果，**不是本次已经运行自动解析器的证明**。例如 Wireframe 两种沙发的宽度分别为 1600.2 和 2266.95 mm；Muuto 的高/宽/深为 770/545/560 mm；HAY 所读字段因单位缺失而保持未归一化。

## 官方模型发现的影响

[Aeron 专业资源页](https://www.hermanmiller.com/products/seating/office-chairs/aeron-chair/pro-resources/)已列出按尺寸/扶手配置区分的 Revit 和其他模型。[HAY 规划页](https://www.hay.com/professionals/planning)提供 pCon/Revit 路线，Muuto 产品页也链接建筑师文件。建议让用户可选择已核实的官方资产，或使用 Tripo 生成；官方资产还可作比例、外形和族行为的对照。

仅有下载链接不能证明文件可用、参数完整或兼容 Revit 2025/2026。后续需检验 RFA 类型目录、贴图等依赖，并在真实 Revit 中验证加载、命名、明细表及排布。本轮没有执行付费生成或真实宿主导入。
