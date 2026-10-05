using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityEngine;
using Feif.Extensions;

#if USING_UNITASK
using GameObjectTask = Cysharp.Threading.Tasks.UniTask<UnityEngine.GameObject>;
using UIBaseTask = Cysharp.Threading.Tasks.UniTask<Feif.UIFramework.UIBase>;
using Task = Cysharp.Threading.Tasks.UniTask;
using Cysharp.Threading.Tasks;
#else
using GameObjectTask = System.Threading.Tasks.Task<UnityEngine.GameObject>;
using UIBaseTask = System.Threading.Tasks.Task<Feif.UIFramework.UIBase>;
using Task = System.Threading.Tasks.Task;
using System.Threading.Tasks;
#endif

namespace Feif.UIFramework
{
    [RequireComponent(typeof(RectTransform))]
    [DisallowMultipleComponent]
    public class UIFrame : MonoBehaviour
    {
        // 单例UI实例（用于 Panel / Window 等非 Popup 层）
        private static readonly Dictionary<Type, GameObject> instances = new Dictionary<Type, GameObject>();
        // Popup 层可以同时存在多个同类型实例，使用列表存储
        private static readonly Dictionary<Type, List<GameObject>> popupInstances = new Dictionary<Type, List<GameObject>>();
        private static readonly Stack<(Type type, UIData data)> panelStack = new Stack<(Type, UIData)>();
        private static readonly Dictionary<UILayer, RectTransform> uiLayers = new Dictionary<UILayer, RectTransform>();
        private static HashSet<UITimer> timers = new HashSet<UITimer>();
        private static HashSet<UITimer> timerRemoveSet = new HashSet<UITimer>();
        private static RectTransform layerTransform;

        [SerializeField] private RectTransform layers;
        [SerializeField] private Canvas canvas;

        /// <summary>
        /// UI画布
        /// </summary>
        public static Canvas Canvas { get; private set; }

        /// <summary>
        /// UI相机
        /// </summary>
        public static Camera Camera { get; private set; }

        /// <summary>
        /// 当加载UI超过这个时间（单位：秒）时，检测为卡住
        /// </summary>
        public static float StuckTime = 1;

        /// <summary>
        /// 当前显示的Panel
        /// </summary>
        public static UIBase CurrentPanel
        {
            get
            {
                if (panelStack.Count <= 0) return null;

                if (panelStack.Peek().type == null) return null;

                if (instances.TryGetValue(panelStack.Peek().type, out var instance))
                {
                    return instance.GetComponent<UIBase>();
                }
                return null;
            }
        }

        private void Awake()
        {
            if (canvas == null) throw new Exception("UIFrame初始化失败，请设置Canvas");
            if (canvas.worldCamera == null) throw new Exception("UIFrame初始化失败，请给Canvas设置worldCamera");
            if (layers == null) throw new Exception("UIFrame初始化失败，请设置layers");
            Canvas = canvas;
            Camera = canvas.worldCamera;
            layerTransform = layers;
            layerTransform.anchorMin = Vector2.zero;
            layerTransform.anchorMax = Vector2.one;
            layerTransform.offsetMin = Vector2.zero;
            layerTransform.offsetMax = Vector2.zero;
            DontDestroyOnLoad(gameObject);
            AutoBindUITimer.Enable();
            AutoBindUGUIButtonEvent.Enable();
        }

        #region 事件
        /// <summary>
        /// 卡住开始时触发的事件
        /// </summary>
        public static event Action OnStuckStart;

        /// <summary>
        /// 卡住结束时触发的事件
        /// </summary>
        public static event Action OnStuckEnd;

        /// <summary>
        /// 资源请求
        /// </summary>
        public static event Func<Type, GameObjectTask> OnAssetRequest;

        /// <summary>
        /// 资源释放
        /// </summary>
        public static event Action<Type> OnAssetRelease;

        /// <summary>
        /// UI创建时调用
        /// </summary>
        public static event Action<UIBase> OnCreate;

        /// <summary>
        /// UI刷新时调用
        /// </summary>
        public static event Action<UIBase> OnRefresh;

        /// <summary>
        /// UI绑定事件时调用
        /// </summary>
        public static event Action<UIBase> OnBind;

        /// <summary>
        /// UI解绑事件时调用
        /// </summary>
        public static event Action<UIBase> OnUnbind;

        /// <summary>
        /// UI显示时调用
        /// </summary>
        public static event Action<UIBase> OnShow;

        /// <summary>
        /// UI隐藏时调用
        /// </summary>
        public static event Action<UIBase> OnHide;

        /// <summary>
        /// UI销毁时调用
        /// </summary>
        public static event Action<UIBase> OnDied;
        #endregion

        #region 显示
        /// <summary>
        /// 显示UI
        /// </summary>
        public static UIBaseTask Show(UIBase ui, UIData data = null)
        {
            if (GetLayer(ui) != null && ui.Parent != null) throw new Exception("子UI不能使用UILayer属性");

            return ShowAsync(ui, data);
        }

        /// <summary>
        /// 显示Panel或Window
        /// </summary>
#if USING_UNITASK
        public static UniTask<T> Show<T>(UIData data = null) where T : UIBase
#else
        public static Task<T> Show<T>(UIData data = null) where T : UIBase
#endif
        {
            return ShowAsync<T>(data);
        }

        /// <summary>
        /// 显示Panel或Window
        /// </summary>
        public static UIBaseTask Show(Type type, UIData data = null)
        {
            if (GetLayer(type) == null) throw new Exception("请使用[UILayer]子类标记类，显示子UI请使用Show(UIBase ui)");

            return ShowAsync(type, data);
        }
        #endregion

        #region 隐藏
        /// <summary>
        /// 隐藏Panel
        /// </summary>
        public static Task Hide(bool forceDestroy = false)
        {
            return HideAsync(forceDestroy);
        }

        /// <summary>
        /// 隐藏Panel或Window
        /// </summary>
        public static Task Hide<T>(bool forceDestroy = false)
        {
            return Hide(typeof(T), forceDestroy);
        }

        /// <summary>
        /// 隐藏Panel或Window
        /// </summary>
        public static Task Hide(Type type, bool forceDestroy = false)
        {
            if (GetLayer(type) is PanelLayer)
            {
                if (CurrentPanel != null && CurrentPanel.GetType() == type) return Hide();

                throw new Exception(type.ToString() + "不是当前正在显示的Panel，请使用UIFrame.Hide()来隐藏当前Panel");
            }
            else if (GetLayer(type) != null)
            {
                var layer = GetLayer(type);
                // Popup 层允许多实例，逐个隐藏/销毁
                if (layer is PopupLayer)
                {
                    if (popupInstances.TryGetValue(type, out var list) && list != null)
                    {
                        var removeList = new List<GameObject>();
                        foreach (var go in list.ToArray())
                        {
                            if (go == null) continue;
                            var uibase = go.GetComponent<UIBase>();
                            var uibases = uibase.BreadthTraversal().ToArray();
                            DoUnbind(uibases);
                            DoHide(uibases);
                            go.SetActive(false);
                            if (uibase.AutoDestroy || forceDestroy)
                            {
                                UIFrame.Destroy(go);
                                removeList.Add(go);
                            }
                        }
                        foreach (var r in removeList) list.Remove(r);
                        if (list.Count == 0) popupInstances.Remove(type);
                    }
                    return Task.CompletedTask;
                }

                // 非 Popup（保持原有单例隐藏逻辑）
                if (instances.TryGetValue(type, out var instance))
                {
                    var uibase = instance.GetComponent<UIBase>();
                    var uibases = uibase.BreadthTraversal().ToArray();
                    DoUnbind(uibases);
                    DoHide(uibases);
                    instance.SetActive(false);
                    if (uibase.AutoDestroy || forceDestroy) ReleaseInstance(type);
                }
                return Task.CompletedTask;
            }
            throw new Exception("隐藏UI失败，请使用[UILayer]子类标记类，隐藏子UI请使用UIFrame.Hide(UIBase ui)");
        }

        /// <summary>
        /// 隐藏UI，forceDestroy对子UI无效。
        /// </summary>
        public static Task Hide(UIBase ui, bool forceDestroy = false)
        {
            if (ui == null) return Task.CompletedTask;

            var layer = GetLayer(ui);
            // 子UI（无层）仍按原逻辑：隐藏传入的实例及其子节点
            if (layer == null)
            {
                if (!ui.gameObject.activeSelf) return Task.CompletedTask;

                var uibases = ui.BreadthTraversal().ToArray();
                DoUnbind(uibases);
                DoHide(uibases);
                ui.gameObject.SetActive(false);
                return Task.CompletedTask;
            }

            // 如果是 PopupLayer，仅隐藏传入的实例（允许多实例共存）
            if (layer is PopupLayer)
            {
                if (!ui.gameObject.activeSelf) return Task.CompletedTask;

                var uibases = ui.BreadthTraversal().ToArray();
                DoUnbind(uibases);
                DoHide(uibases);
                ui.gameObject.SetActive(false);

                if (ui.AutoDestroy || forceDestroy)
                {
                    // 仅销毁该实例（Destroy 会从 popupInstances 中移除引用）
                    UIFrame.Destroy(ui.gameObject);
                }
                return Task.CompletedTask;
            }

            // 其他有层的 UI 保持原有按类型隐藏逻辑（单例行为）
            return Hide(ui.GetType(), forceDestroy);
        }
        #endregion

        #region 获得
        /// <summary>
        /// 获得已经实例化的UI
        /// </summary>
        public static UIBase Get(Type type)
        {
            if (type == null) return null;
            // 优先返回 Popup 类型的最后一个实例（如果存在）
            if (popupInstances.TryGetValue(type, out var list) && list != null && list.Count > 0)
            {
                var last = list[list.Count - 1];
                if (last != null) return last.GetComponent<UIBase>();
            }
            if (instances.TryGetValue(type, out var instance))
            {
                return instance.GetComponent<UIBase>();
            }
            return null;
        }

        /// <summary>
        /// 获得已经实例化的UI
        /// </summary>
        public static UIBase Get<T>()
        {
            return Get(typeof(T));
        }

        /// <summary>
        /// 获得已经实例化的UI
        /// </summary>
        public static bool TryGet<T>(out UIBase ui)
        {
            ui = Get<T>();
            return ui != null;
        }

        /// <summary>
        /// 获得已经实例化的UI
        /// </summary>
        public static bool TryGet(Type type, out UIBase ui)
        {
            ui = Get(type);
            return ui != null;
        }

        /// <summary>
        /// 获得所有已经实例化的UI
        /// </summary>
        public static IEnumerable<UIBase> GetAll(Func<Type, bool> predicate = null)
        {
            foreach (var item in instances)
            {
                if (predicate != null && !predicate.Invoke(item.Key)) continue;

                yield return item.Value.GetComponent<UIBase>();
            }
            // 包含 Popup 多实例
            foreach (var item in popupInstances)
            {
                if (predicate != null && !predicate.Invoke(item.Key)) continue;
                foreach (var go in item.Value)
                {
                    if (go == null) continue;
                    yield return go.GetComponent<UIBase>();
                }
            }
        }

        /// <summary>
        /// 获得UILayer
        /// </summary>
        public static UILayer GetLayer(Type type)
        {
            if (type == null) return null;

            var layer = type.GetCustomAttributes(typeof(UILayer), true).FirstOrDefault() as UILayer;
            return layer;
        }

        /// <summary>
        /// 获得UILayer
        /// </summary>
        public static UILayer GetLayer(UIBase ui)
        {
            return GetLayer(ui.GetType());
        }

        /// <summary>
        /// 获得UI层RectTransform
        /// </summary>
        public static RectTransform GetLayerTransform(Type type)
        {
            var layer = GetLayer(type);
            uiLayers.TryGetValue(layer, out var result);
            return result;
        }
        #endregion

        #region 刷新
        /// <summary>
        /// 刷新UI。data为null时，将用之前的data刷新
        /// </summary>
        public static Task Refresh<T>(UIData data = null)
        {
            return Refresh(typeof(T), data);
        }

        /// <summary>
        /// 刷新UI。data为null时将用之前的data刷新
        /// </summary>
        public static Task Refresh(Type type, UIData data = null)
        {
            if (type == null) return Task.CompletedTask;
            if (instances.TryGetValue(type, out var instance))
            {
                return Refresh(instance.GetComponent<UIBase>(), data);
            }
            if (popupInstances.TryGetValue(type, out var list) && list != null && list.Count > 0)
            {
                var last = list[list.Count - 1];
                if (last != null) return Refresh(last.GetComponent<UIBase>(), data);
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// 刷新UI。data为null时将用之前的data刷新
        /// </summary>
        public static Task Refresh(UIBase ui, UIData data = null)
        {
            if (!ui.gameObject.activeInHierarchy) return Task.CompletedTask;

            var uibases = ui.BreadthTraversal().ToArray();
            if (data != null) TrySetData(ui, data);
            if (panelStack.Count > 0 && GetLayer(ui) is PanelLayer)
            {
                var (type, _) = panelStack.Pop();
                panelStack.Push((type, data));
            }
            return DoRefresh(uibases);
        }

        /// <summary>
        /// 刷新所有UI
        /// </summary>
        public static async Task RefreshAll(Func<Type, bool> predicate = null)
        {
            foreach (var item in instances)
            {
                if (predicate != null && !predicate.Invoke(item.Key)) continue;

                await Refresh(item.Value.GetComponent<UIBase>());
            }
            // 刷新 Popup 多实例
            foreach (var kv in popupInstances)
            {
                if (predicate != null && !predicate.Invoke(kv.Key)) continue;
                foreach (var go in kv.Value)
                {
                    if (go == null) continue;
                    await Refresh(go.GetComponent<UIBase>());
                }
            }
        }
        #endregion

        /// <summary>
        /// 创建UI GameObject
        /// </summary>
        public static GameObjectTask Instantiate(GameObject prefab, Transform parent = null, UIData data = null)
        {
            return InstantiateAsync(prefab, parent, data);
        }

        /// <summary>
        /// 销毁UI GameObject
        /// </summary>
        public static void Destroy(GameObject instance)
        {
            var uibases = instance.transform.BreadthTraversal()
                .Where(item => item.GetComponent<UIBase>() != null)
                .Select(item => item.GetComponent<UIBase>())
                .ToArray();
            var parentUI = GetParent(uibases.FirstOrDefault());
            foreach (var item in uibases)
            {
                if (parentUI == null) break;

                if (GetParent(item) != parentUI) break;

                parentUI.Children.Remove(item);
            }
            foreach (var item in uibases)
            {
                try
                {
                    OnDied?.Invoke(item);
                    item.InnerOnDied();
                    item.CancelAllTimer();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }

            // 移除 popupInstances 中可能存在的引用，避免悬空引用
            try
            {
                if (uibases != null && uibases.Length > 0)
                {
                    var root = uibases[0];
                    if (root != null)
                    {
                        var type = root.GetType();
                        lock (popupInstances)
                        {
                            if (popupInstances.TryGetValue(type, out var list) && list != null)
                            {
                                if (list.Contains(instance)) list.Remove(instance);
                                if (list.Count == 0) popupInstances.Remove(type);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }

            GameObject.Destroy(instance);
        }

        /// <summary>
        /// 立即销毁UI GameObject
        /// </summary>
        public static void DestroyImmediate(GameObject instance)
        {
            var uibases = instance.transform.BreadthTraversal()
               .Where(item => item.GetComponent<UIBase>() != null)
               .Select(item => item.GetComponent<UIBase>())
               .ToArray();
            var parentUI = GetParent(uibases.FirstOrDefault());
            foreach (var item in uibases)
            {
                if (parentUI == null) break;

                if (GetParent(item) != parentUI) break;

                parentUI.Children.Remove(item);
            }
            foreach (var item in uibases)
            {
                try
                {
                    OnDied?.Invoke(item);
                    item.InnerOnDied();
                    item.CancelAllTimer();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }
            GameObject.DestroyImmediate(instance);
        }

        /// <summary>
        /// 强制释放已经关闭的UI，即使UI的AutoDestroy为false，仍然释放该资源
        /// </summary>
        public static void Release()
        {
            var keys = new List<Type>();
            foreach (var item in instances)
            {
                if (item.Value != null && !item.Value.activeInHierarchy)
                {
                    UIFrame.Destroy(item.Value);
                    OnAssetRelease?.Invoke(item.Key);
                    keys.Add(item.Key);
                }
            }
            foreach (var item in keys)
            {
                instances.Remove(item);
            }

            // 处理 Popup 多实例的释放：移除所有已经不活跃的实例
            var popupRemoveTypes = new List<Type>();
            foreach (var kv in popupInstances)
            {
                var list = kv.Value;
                if (list == null || list.Count == 0) { popupRemoveTypes.Add(kv.Key); continue; }
                var toRemove = new List<GameObject>();
                foreach (var go in list)
                {
                    if (go == null || !go.activeInHierarchy)
                    {
                        if (go != null) UIFrame.Destroy(go);
                        toRemove.Add(go);
                    }
                }
                foreach (var go in toRemove) list.Remove(go);
                if (list.Count == 0) popupRemoveTypes.Add(kv.Key);
            }
            foreach (var t in popupRemoveTypes)
            {
                popupInstances.Remove(t);
                OnAssetRelease?.Invoke(t);
            }
        }

        /// <summary>
        /// 创建定时器
        /// </summary>
        /// <param name="delay">延迟多少秒后执行callback</param>
        /// <param name="callback">延迟执行的方法</param>
        /// <param name="isLoop">是否是循环定时器</param>
        public static UITimer CreateTimer(float delay, Action callback, bool isLoop = false)
        {
            if (delay <= 0) throw new Exception("delay必须大于0");
            var timer = new UITimer(delay, callback, isLoop);
            timers.Add(timer);
            return timer;
        }

        private static async GameObjectTask RequestInstance(Type type, UIData data)
        {
            if (type == null) throw new NullReferenceException();

            // 如果是 Popup 层，则允许创建多个实例
            var layer = GetLayer(type);
            if (layer is PopupLayer)
            {
                GameObject refInstance = null;
                if (OnAssetRelease != null)
                {
                    refInstance = await OnAssetRequest.Invoke(type);
                }
                var uibase = refInstance.GetComponent<UIBase>();
                if (uibase == null) throw new Exception("预制体没有挂载继承自UIBase的脚本");
                var parent = GetOrCreateLayerTransform(type);
                var instance = await UIFrame.Instantiate(refInstance, parent, data);

                lock (popupInstances)
                {
                    if (!popupInstances.TryGetValue(type, out var list) || list == null)
                    {
                        list = new List<GameObject>();
                        popupInstances[type] = list;
                    }
                    list.Add(instance);
                }
                return instance;
            }

            // 非 Popup（保持原有单例逻辑）
            if (instances.TryGetValue(type, out var instanceObj))
            {
                TrySetData(instanceObj.GetComponent<UIBase>(), data);
                return instanceObj;
            }
            GameObject refInst = null;
            if (OnAssetRelease != null)
            {
                refInst = await OnAssetRequest.Invoke(type);
            }
            var uibase2 = refInst.GetComponent<UIBase>();
            if (uibase2 == null) throw new Exception("预制体没有挂载继承自UIBase的脚本");
            var parent2 = GetOrCreateLayerTransform(type);
            instanceObj = await UIFrame.Instantiate(refInst, parent2, data);

            // 处理并发创建问题：如果在当前请求过程中另一个请求已经创建并注册了该类型的实例，
            // 则销毁当前重复实例并返回已注册的实例，避免场景中存在多个相同类型的活跃实例
            lock (instances)
            {
                if (instances.TryGetValue(type, out var existing) && existing != null)
                {
                    TrySetData(existing.GetComponent<UIBase>(), data);
                    UIFrame.Destroy(instanceObj);
                    instanceObj = existing;
                }
                else
                {
                    instances[type] = instanceObj;
                }
            }
            return instanceObj;
        }

        private static void ReleaseInstance(Type type)
        {
            if (type == null) return;

            var layer = GetLayer(type);
            if (layer is PopupLayer)
            {
                if (popupInstances.TryGetValue(type, out var list) && list != null)
                {
                    foreach (var go in list)
                    {
                        if (go == null) continue;
                        UIFrame.Destroy(go);
                    }
                    popupInstances.Remove(type);
                    OnAssetRelease?.Invoke(type);
                }
                return;
            }

            if (instances.TryGetValue(type, out var instance))
            {
                var root = instance.GetComponent<UIBase>();
                UIFrame.Destroy(instance);
                OnAssetRelease?.Invoke(type);
                instances.Remove(type);
            }
        }

        private static async Task DoRefresh(IList<UIBase> uibases)
        {
            if (uibases == null) return;

            for (int i = 0; i < uibases.Count; ++i)
            {
                if (uibases[i] == null) continue;

                if (i == 0 || uibases[i].gameObject.activeSelf)
                {
                    try
                    {
                        OnRefresh?.Invoke(uibases[i]);
                        await uibases[i].InnerOnRefresh();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex);
                    }
                }
            }
        }

        private static void DoBind(IList<UIBase> uibases)
        {
            if (uibases == null) return;

            for (int i = 0; i < uibases.Count; ++i)
            {
                if (uibases[i] == null) continue;

                if (i == 0 || uibases[i].gameObject.activeSelf)
                {
                    try
                    {
                        OnBind?.Invoke(uibases[i]);
                        uibases[i].InnerOnBind();

                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex);
                    }
                }
            }
        }

        private static void DoUnbind(IList<UIBase> uibases)
        {
            if (uibases == null) return;

            for (int i = uibases.Count - 1; i >= 0; --i)
            {
                if (uibases[i] == null) continue;

                if (i == 0 || uibases[i].gameObject.activeSelf)
                {
                    try
                    {
                        OnUnbind?.Invoke(uibases[i]);
                        uibases[i].InnerOnUnbind();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex);
                    }
                }
            }
        }

        private static void DoShow(IList<UIBase> uibases)
        {
            if (uibases == null) return;

            for (int i = 0; i < uibases.Count; ++i)
            {
                if (uibases[i] == null) continue;

                if (i == 0 || uibases[i].gameObject.activeSelf)
                {
                    try
                    {
                        OnShow?.Invoke(uibases[i]);
                        uibases[i].InnerOnShow();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex);
                    }
                }
            }
        }

        private static void DoHide(IList<UIBase> uibases)
        {
            if (uibases == null) return;

            for (int i = uibases.Count - 1; i >= 0; --i)
            {
                if (uibases[i] == null) continue;

                if (i == 0 || uibases[i].gameObject.activeSelf)
                {
                    try
                    {
                        OnHide?.Invoke(uibases[i]);
                        uibases[i].InnerOnHide();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex);
                    }
                }
            }
        }

        private static async GameObjectTask InstantiateAsync(GameObject prefab, Transform parent, UIData data)
        {
            bool refActiveSelf = prefab.activeSelf;
            prefab.SetActive(false);
            var instance = GameObject.Instantiate(prefab, parent);
            prefab.SetActive(refActiveSelf);
            var uibase = instance.GetComponent<UIBase>();
            var uibases = instance.transform.BreadthTraversal()
                .Where(item => item.GetComponent<UIBase>() != null)
                .Select(item => item.GetComponent<UIBase>())
                .ToArray();
            TrySetData(instance.GetComponent<UIBase>(), data);
            foreach (var item in uibases)
            {
                item.Children.Clear();
            }
            foreach (var item in uibases)
            {
                var parentUI = GetParent(item);
                if (parentUI == null) continue;
                parentUI.Children.Add(item);
                item.Parent = parentUI;
            }
            foreach (var item in uibases)
            {
                try
                {
                    OnCreate?.Invoke(item);
                    await item.InnerOnCreate();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }
            if (GetLayer(uibase) == null)
            {
                await DoRefresh(uibases);
                instance.SetActive(true);
                DoBind(uibases);
                DoShow(uibases);
            }
            return instance;
        }

        private static async UIBaseTask ShowAsync(UIBase ui, UIData data = null)
        {
            try
            {
                if (GetLayer(ui) == null)
                {
                    if (ui.gameObject.activeSelf) return ui;

                    TrySetData(ui, data);
                    var timeout = new CancellationTokenSource();
                    bool isStuck = false;
                    Task.Delay(TimeSpan.FromSeconds(StuckTime)).GetAwaiter().OnCompleted(() =>
                    {
                        if (timeout.IsCancellationRequested) return;

                        OnStuckStart?.Invoke();
                        isStuck = true;
                    });
                    var parentUIBases = ui.Parent.BreadthTraversal().ToArray();
                    DoUnbind(parentUIBases);
                    var uibases = ui.BreadthTraversal().ToArray();
                    await DoRefresh(uibases);
                    ui.gameObject.SetActive(true);
                    if (ui.Parent != null)
                    {
                        DoBind(parentUIBases);
                    }
                    else
                    {
                        DoBind(uibases);
                    }
                    DoShow(uibases);
                    timeout.Cancel();

                    if (isStuck) OnStuckEnd?.Invoke();

                    return ui;
                }
                return await Show(ui.GetType(), data);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                return null;
            }
        }

#if USING_UNITASK
        private static async UniTask<T> ShowAsync<T>(UIData data = null) where T : UIBase
#else
        private static async Task<T> ShowAsync<T>(UIData data = null) where T : UIBase
#endif
        {
            var result = await Show(typeof(T), data);
            return result as T;
        }

        private static async UIBaseTask ShowAsync(Type type, UIData data = null)
        {
            try
            {
                var timeout = new CancellationTokenSource();
                UIBase result = null;
                bool isStuck = false;
                Task.Delay(TimeSpan.FromSeconds(StuckTime)).GetAwaiter().OnCompleted(() =>
                {
                    if (timeout.IsCancellationRequested) return;
                    OnStuckStart?.Invoke();
                    isStuck = true;
                });
                if (GetLayer(type) is PanelLayer)
                {
                    var previousPanel = CurrentPanel;
                    if (previousPanel != null && type == previousPanel.GetType()) return previousPanel;

                    UIBase[] currentUIBases = null;
                    if (previousPanel != null)
                    {
                        currentUIBases = previousPanel.BreadthTraversal().ToArray();
                        DoUnbind(currentUIBases);
                    }
                    var instanceGO = await RequestInstance(type, data);
                    var uibases = instanceGO.GetComponent<UIBase>().BreadthTraversal().ToArray();
                    if (data != null && previousPanel != null)
                    {
                        data.Sender = previousPanel.GetType();
                    }
                    await DoRefresh(uibases);
                    if (previousPanel != null)
                    {
                        DoHide(currentUIBases);
                        previousPanel.gameObject.SetActive(false);
                        if (previousPanel.AutoDestroy) ReleaseInstance(previousPanel.GetType());
                        if (previousPanel.SkipReturnWhenCovered && panelStack.Count > 0 && panelStack.Peek().type == previousPanel.GetType())
                        {
                            panelStack.Pop();
                        }
                    }
                    instanceGO.SetActive(true);
                    panelStack.Push((type, data));
                    DoBind(uibases);
                    DoShow(uibases);
                    result = instanceGO.GetComponent<UIBase>();
                }
                else if (GetLayer(type) != null)
                {
                    var instanceGO = await RequestInstance(type, data);
                    var uibases = instanceGO.GetComponent<UIBase>().BreadthTraversal().ToArray();

                    if (data != null && CurrentPanel != null)
                    {
                        data.Sender = CurrentPanel.GetType();
                    }
                    await DoRefresh(uibases);
                    instanceGO.SetActive(true);
                    instanceGO.transform.SetAsLastSibling();
                    DoBind(uibases);
                    DoShow(uibases);
                    result = instanceGO.GetComponent<UIBase>();
                }
                timeout.Cancel();
                if (isStuck) OnStuckEnd?.Invoke();
                return result;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                return null;
            }
        }

        private static async Task HideAsync(bool forceDestroy)
        {
            try
            {
                var timeout = new CancellationTokenSource();

                bool isStuck = false;
                Task.Delay(TimeSpan.FromSeconds(StuckTime)).GetAwaiter().OnCompleted(() =>
                {
                    if (timeout.IsCancellationRequested) return;
                    OnStuckStart?.Invoke();
                    isStuck = true;
                });

                if (CurrentPanel == null)
                {
                    timeout.Cancel();
                    return;
                }

                var currentPanel = CurrentPanel;
                var currentUIBases = currentPanel.BreadthTraversal().ToArray();
                panelStack.Pop();
                DoUnbind(currentUIBases);
                if (panelStack.Count > 0)
                {
                    var data = panelStack.Peek().data;
                    if (data != null && currentPanel != null)
                    {
                        data.Sender = currentPanel.GetType();
                    }
                    var instance = await RequestInstance(panelStack.Peek().type, data);
                    var uibases = instance.GetComponent<UIBase>().BreadthTraversal().ToArray();
                    await DoRefresh(uibases);
                    currentPanel.gameObject.SetActive(false);
                    DoHide(currentUIBases);
                    if (currentPanel.AutoDestroy || forceDestroy) ReleaseInstance(currentPanel.GetType());
                    instance.SetActive(true);
                    instance.transform.SetAsLastSibling();
                    DoBind(uibases);
                    DoShow(uibases);
                }
                else
                {
                    currentPanel.gameObject.SetActive(false);
                    DoHide(currentUIBases);
                    if (currentPanel.AutoDestroy || forceDestroy) ReleaseInstance(currentPanel.GetType());
                }
                timeout.Cancel();
                if (isStuck) OnStuckEnd?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        private static bool TrySetData(UIBase ui, UIData data)
        {
            if (ui == null) return false;
            var property = ui.GetType().GetProperty("Data", BindingFlags.Public | BindingFlags.Instance);
            if (property == null) return false;
            property.SetValue(ui, data);
            return true;
        }

        private static UIBase GetParent(UIBase ui)
        {
            if (ui == null) return null;

            var parent = ui.transform.parent;
            while (parent != null)
            {
                var uibase = parent.GetComponent<UIBase>();
                if (uibase != null) return uibase;
                parent = parent.parent;
            }
            return null;
        }

        private static RectTransform GetOrCreateLayerTransform(Type type)
        {
            var layer = GetLayer(type);
            if (!uiLayers.TryGetValue(layer, out var result))
            {
                var layerObject = new GameObject(layer.GetName());
                layerObject.transform.SetParent(layerTransform);
                result = layerObject.AddComponent<RectTransform>();
                result.anchorMin = Vector2.zero;
                result.anchorMax = Vector2.one;
                result.offsetMin = Vector2.zero;
                result.offsetMax = Vector2.zero;
                result.localScale = Vector3.one;
                uiLayers[layer] = result;
                int index = 0;
                foreach (var item in uiLayers.OrderBy(i => i.Key.GetOrder()))
                {
                    item.Value.SetSiblingIndex(++index);
                }
            }
            return result;
        }

        private void Update()
        {
            foreach (var item in timers)
            {
                item.Update();
                if (item.IsCancel) timerRemoveSet.Add(item);
            }
            foreach (var item in timerRemoveSet)
            {
                timers.Remove(item);
            }
        }

        private void OnDestroy()
        {
            AutoBindUITimer.Disable();
            AutoBindUGUIButtonEvent.Disable();
        }
    }
}
