namespace Feif.UIFramework
{
    /// <summary>
    /// Popup 层：优先级高于 WindowLayer 的弹出层级（显示在最顶层）
    /// 标记为此层的 UI 允许同时存在多个实例（与 Panel/Window 的行为区分开）。
    /// </summary>
    public class PopupLayer : UILayer
    {
        public override string GetName() => "PopupLayer";
        // 设为 250，确保 Popup 显示在 Window 之上
        public override int GetOrder() => 250;
    }
}