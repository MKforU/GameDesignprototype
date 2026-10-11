# unity动作资源浏览

Unity 编辑器内的游戏动作（Animation）预览工具。单文件 EditorWindow，无其他代码依赖。

## 功能

- 内置默认男/女角色模型（boy / girl prefab，可切换或同屏显示为玩家 1 / 玩家 2）
- 自动扫描工程内 `Animation/male`、`Animation/female` 两个目录的动作资源（约 2000 个 .clip），支持搜索和按性别筛选
- 按命名规则自动分为 10 个分类（舞蹈 / 双人互动 / 待机基础 / 家具场景 / 骑乘 / 换装展示 / 表情情绪 / 比赛结算 / 乐器演出 / 其他玩法），支持分类筛选
- 动作列表显示中文名（读取工程内动画卡片配置表，缺失时自动降级为只显示英文名）
- 单击动作即可在独立预览窗口中播放预览
- 支持播放 / 暂停、循环、变速、进度拖拽、视角旋转缩放
- **曲线绑定诊断**：选中动作时统计 clip 曲线路径在当前骨架上的绑定数（底栏 `已绑定/总数`，缺失标红），预览区顶部同步给出红字警告

## 使用方法

1. 将 `AnimPreviewWindow.cs` 放入目标 Unity 工程的任意 `Editor` 文件夹下（**必须在 Editor 目录内**，脚本仅编辑器运行）
2. 等待 Unity 编译完成
3. 从菜单栏 **Tools → 动作预览器** 打开

`AnimPreviewWindow.cs.meta` 为 Unity 自动生成的资产元数据，建议一并复制；新工程中 Unity 也会重新生成。

## 环境依赖

- Unity 2022.3.62f3（开发版本，其他 2022.3.x 及以上版本预计兼容）
- 仅依赖 Unity 内置 API（`PreviewRenderUtility` + `Animation.Sample` + `AnimationUtility.GetCurveBindings`），**无第三方包**

## 曲线绑定诊断说明

Unity 对找不到目标路径的动画曲线是**静默丢弃**的：表现为「进度条在走、模型不跟着动」，容易误判成预览工具坏了。

选中动作后，工具会把 clip 里每条曲线路径按 legacy Animation 的规则（相对 Animation 宿主 `Transform.Find`）解析一遍：

- 底栏显示 `P1:动作名 168/168`（绿色 = 全部绑定正常）
- 有缺失时标红，如 `4/52`，并在预览区顶部提示 `P1 仅绑定 4/52 条曲线 — 曲线路径与骨架不匹配，缺失部分不会动`

典型原因是动作 clip 按旧版骨架导出（缺少新增骨骼层级、骨骼挂点变更），需要按当前骨架重导该动作。

## 注意事项

脚本顶部的路径常量（模型 prefab、动作目录与中文名配置表）为占位示例（**已做模糊处理，不指向任何真实工程**）：

```csharp
const string kMaleFolder   = "Assets/<你的工程>/Animation/male";
const string kFemaleFolder = "Assets/<你的工程>/Animation/female";
const string kBoyPath      = "Assets/<你的工程>/Prefab/boy.prefab";
const string kGirlPath     = "Assets/<你的工程>/Prefab/girl.prefab";
```

`LoadCnNames()` 内的配置表路径同样为占位：

```csharp
string path = Path.Combine(Application.dataPath, "<你的工程>/Config/Tbl/t_animation_card.txt");
```

使用时改成你工程里实际的路径即可。配置表缺失时中文名功能自动降级，不影响其余功能。
