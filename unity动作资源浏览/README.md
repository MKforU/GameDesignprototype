# unity动作资源浏览

Unity 编辑器内的游戏动作（Animation）预览工具。单文件 EditorWindow，无其他代码依赖。

## 功能

- 内置默认男/女角色模型（boy / girl prefab，可切换或同屏显示为玩家 1 / 玩家 2）
- 自动扫描工程内 `Animation/male`、`Animation/female` 两个目录的动作资源（约 2000 个 .clip），支持搜索和按性别筛选
- 单击动作即可在独立预览窗口中播放预览
- 支持播放 / 暂停、循环、变速、进度拖拽、视角旋转缩放

## 使用方法

1. 将 `AnimPreviewWindow.cs` 放入目标 Unity 工程的任意 `Editor` 文件夹下（**必须在 Editor 目录内**，脚本仅编辑器运行）
2. 等待 Unity 编译完成
3. 从菜单栏 **Tools → 动作预览器** 打开

`AnimPreviewWindow.cs.meta` 为 Unity 自动生成的资产元数据，建议一并复制；新工程中 Unity 也会重新生成。

## 环境依赖

- Unity 2022.3.62f3（开发版本，其他 2022.3.x 及以上版本预计兼容）
- 仅依赖 Unity 内置 API（`PreviewRenderUtility` + `Animation.Sample`），**无第三方包**

## 注意事项

脚本顶部的路径常量（模型 prefab 与动作目录）为占位示例（**已做模糊处理，不指向任何真实工程**）：

```csharp
const string kMaleFolder   = "Assets/<你的工程>/Animation/male";
const string kFemaleFolder = "Assets/<你的工程>/Animation/female";
const string kBoyPath      = "Assets/<你的工程>/Prefab/boy.prefab";
const string kGirlPath     = "Assets/<你的工程>/Prefab/girl.prefab";
```

使用时改成你工程里实际的动作目录和角色 prefab 路径即可（与脚本内默认值对应）。
