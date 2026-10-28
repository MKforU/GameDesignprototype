# unity服装资源浏览

Unity 编辑器内的游戏服装资源浏览与试穿工具，面向策划：图标网格浏览、按套装聚合、男女双角色 3D 试穿、配置表校验、投放状态导入。共 3 个脚本，编辑器专用。

## 功能

- **分类浏览**：全部 / 有套装 / 无套装三个页签，支持部位、性别、星级（t_item.f_LabelType：未配置/二星/三星/四星/五星）、关键词、排序筛选；带套装的单品在网格中自动聚合为一张套装卡片（只显示上衣/连身主件图标），点击卡片一键穿整套，右侧面板列出该套装所有部位与道具 ID
- **3D 试穿**：男女双角色同屏预览（`PreviewRenderUtility`），点击单品即穿；穿套装时按组 ID 同编号自动配对男女两侧（`m_0039` ↔ `f_0039`，同名兜底）；支持待机动画播放、恢复默认装扮；预览区与网格之间可拖拽调整高度（会记忆）；相机操作对齐 Scene 视图：右键按住 + WASD/QE/Shift 漫游，Alt+左键环绕，中键平移，滚轮缩放，F 重置视角
- **穿戴逻辑对齐游戏**：带 `FBSkinnedInfo` 的服装按骨骼名绑骨，并按 `ClothConfigData.DynamicBoneInfos` 创建运行时动态骨骼（裤子/头发的 `Bone_*_sb_*`）；自带骨骼的饰品（翅膀/耳环/肩饰等）按 `t_america_equip_position` 挂点挂载；服装粒子特效按 `ClothConfigData.Effects` 创建，编辑态用 `ParticleSystem.Simulate` 手动驱动
- **配置校验**：比对 t_item / t_equip / t_mall / t_pokedex，输出缺图标、缺 t_equip 记录、套装件数异常、图鉴引用缺失等问题清单，可一键复制/导出
- **投放状态**：导入策划投放统计 xlsx（「服饰套装」表：E 列 = 男件 ID、L 列 = 男件投放；P 列 = 女件 ID、W 列 = 女件投放，**非空即已对外**，内容作为渠道保留），卡片角标显示 已投放 / 部分 / 未投放 / 表外，可按投放状态筛选；结果缓存在工程 `Library/` 下，支持重复导入更新
- **特效开关**：全局特效总开关之外，支持按件开关试穿特效（详情面板「试穿特效」，同编号套装 `m_0007 ↔ f_0007` 男女联动关闭/打开，重新打开时粒子自动重播）；带特效的服装卡片标 **FX** 角标，方便一眼识别

## 使用方法

1. 将 3 个 `.cs` 放入目标 Unity 工程的任意 `Editor` 文件夹（**必须在 Editor 目录内**），`.meta` 建议一并复制
2. 等待 Unity 编译完成
3. 从菜单栏 **Tools → 服装资源预览** 打开
4. （可选）点工具栏「导入投放表」选择投放统计 xlsx，导入后可按投放状态筛选

## 环境依赖

- Unity 2022.3.62f3（开发版本，其他 2022.3.x 及以上预计兼容）
- 读取 xlsx 使用工程内置的 **EPPlus**（若工程没有，需自行引入，或删除投放导入相关代码）
- 其余仅依赖 Unity 内置 API（`PreviewRenderUtility`、`Animation.Sample`、`ParticleSystem.Simulate`），无第三方包

## 注意事项

脚本顶部的路径常量为占位示例（**已做模糊处理，不指向任何真实工程**），使用时改成你工程里的实际目录：

```csharp
// ClothTryOnPreview.cs —— 角色/服装/特效
MaleRigPath / FemaleRigPath        // 男女角色模型（含骨骼与默认部件的完整身体 prefab）
MaleIdlePath / FemaleIdlePath      // 男女待机动画 .anim
ClothPrefabFolder                  // 服装 prefab 目录
ClothLowPrefabFolder               // 低模服装 prefab（回退查找）
ModelEquipFolder                   // 自带骨骼的饰品 prefab 目录
EffectClothFolder                  // 服装特效 prefab 目录

// ClothResourcePreviewWindow.cs —— 图标
IconFolderItem                     // 物品图标目录
IconFolderPinch                    // 脸部（捏脸）图标目录
```

```csharp
const string MaleRigPath = "Assets/<你的工程>/Asset/NewRoleAsset/Role/Cloth_H/b_cloth_1000000/b_cloth_1000000.prefab";
const string ClothPrefabFolder = "Assets/<你的工程>/Asset/BuildAssets/Clothes/Prefab";
```

## 口径说明

- **套装判定**：`t_item.f_GroupID` 非空即为有套装（形如 `m_5001` / `f_5011`，可 `|` 分隔多组）；不使用 `t_equip.f_SuitID`（可能全表为 0）
- **穿戴互斥**：同部位或同互斥组先卸再穿（{发型,上全身,整全身}、{上衣,连身,上全身,整全身}、{下衣,连身,整全身}、{鞋,整全身}、{手套,整全身}）
- **特效显示**：`t_equip.f_CanEffect == 0` 时游戏内不显示特效，工具会照常显示并给出提示，详情面板标注游戏内开关状态
