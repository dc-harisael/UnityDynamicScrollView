// -----------------------------------------------------------------------
// <copyright file="ScrollView.cs" company="AillieoTech">
// Copyright (c) AillieoTech. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AillieoUtils
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using UnityEngine;
    using UnityEngine.Serialization;
    using UnityEngine.UI;

    [RequireComponent(typeof(RectTransform))]
    [DisallowMultipleComponent]
    public partial class ScrollView : ScrollRect
    {
        [Tooltip("默认item尺寸")]
        public Vector2 defaultItemSize;

        [Tooltip("item的模板")] public RectTransform itemTemplate;

        [Tooltip("Content padding")]
        [SerializeField]
        public RectOffset padding = new();

        [Tooltip("Space between items")]
        [SerializeField]
        public Vector2 spacing = Vector2.zero;

        /// <summary>
        /// Bitmask for scroll direction
        /// </summary>
        protected const int FlagScrollDirection = 1; // 0001

        [SerializeField]
        [FormerlySerializedAs("m_layoutType")]
        protected ItemLayoutType layoutType = ItemLayoutType.Vertical;

        // 只保存4个临界index
        protected int[] criticalItemIndex = new int[4];

        // callbacks for items
        protected Action<int, RectTransform> updateFunc;
        protected Func<int, Vector2> itemSizeFunc;
        protected Func<int> itemCountFunc;
        protected Func<int, RectTransform> itemGetFunc;
        protected Action<RectTransform> itemRecycleFunc;


        private readonly List<ScrollItemWithRect> managedItems = new();

        /// <summary>
        /// Viewport's rectangle but transformed into Content's object space.
        /// </summary>
        private Rect refRect;

        // resource management
        private SimpleObjPool<RectTransform> itemPool = null;

        private int dataCount = 0;

        [Tooltip("初始化时池内item数量")]
        [SerializeField]
        private int poolSize;

        // status
        private bool initialized = false;

        /// <summary>
        /// Bitmask that manages pending data updates for the ScrollView.
        /// It tracks whether an update is needed, the type of update (full or incremental),
        /// and helps defer updates to optimize performance.<br/><br/>
        /// Bits:
        /// <list type="bullet">
        /// <item><description>Bit 0 (value 1): If set, a data update (either incremental or full) is pending.</description></item>
        /// <item><description>Bit 1 (value 2): If set, indicates a full update where all item layouts (rects)
        ///                    are considered dirty and will be recalculated.
        /// </description></item>
        /// </list>
        ///
        /// Common States:
        /// <list type="bullet">
        /// <item><description>0: No update needed.</description></item>
        /// <item><description>1 (0001): Incremental update pending. Item layouts are preserved if possible.</description></item>
        /// <item><description>3 (0011): Full update pending. All item layouts are recalculated.</description></item>
        /// </list>
        /// </summary>
        private int willUpdateData = 0;

        private static readonly Vector3[] viewWorldCorners = new Vector3[4];
        private Vector3[] rectCorners = new Vector3[2];

        private bool applicationIsQuitting;

        private Coroutine delayedUpdateCoroutine;

        // for hide and show
        public enum ItemLayoutType
        {
            // 最后一位表示滚动方向
            Vertical = 0b0001, // 0001
            Horizontal = 0b0010, // 0010
            VerticalThenHorizontal = 0b0100, // 0100
            HorizontalThenVertical = 0b0101, // 0101
            VerticalBottomUp = 0b0111, // 0111
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            if (this.willUpdateData != 0)
            {
                this.StartCoroutine(this.DelayUpdateData());
            }
        }

        protected override void OnDisable()
        {
            this.initialized = false;
            base.OnDisable();
        }

        protected override void OnDestroy()
        {
            if (this.itemPool != null)
            {
                this.itemPool.Purge();
                this.itemPool = null;
            }

            base.OnDestroy();
        }

        private void OnApplicationQuit()
        {
            this.applicationIsQuitting = true;
        }

        protected override void SetContentAnchoredPosition(Vector2 position)
        {
            base.SetContentAnchoredPosition(position);

            if (this.willUpdateData != 0)
            {
                return;
            }

            this.UpdateCriticalItems();
        }

        protected override void SetNormalizedPosition(float value, int axis)
        {
            base.SetNormalizedPosition(value, axis);

            if (this.willUpdateData != 0)
            {
                return;
            }

            this.ResetCriticalItems();
        }

        //---- Public APIs ----
        public virtual void SetUpdateFunc(Action<int, RectTransform> func)
        {
            this.updateFunc = func;
        }

        public virtual void SetItemSizeFunc(Func<int, Vector2> func)
        {
            this.itemSizeFunc = func;
        }

        public virtual void SetItemCountFunc(Func<int> func)
        {
            this.itemCountFunc = func;
        }

        public void SetItemGetAndRecycleFunc(Func<int, RectTransform> getFunc, Action<RectTransform> recycleFunc)
        {
            this.itemGetFunc = getFunc;
            this.itemRecycleFunc = recycleFunc;
        }

        public void ResetAllDelegates()
        {
            this.SetUpdateFunc(null);
            this.SetItemSizeFunc(null);
            this.SetItemCountFunc(null);
            this.SetItemGetAndRecycleFunc(null, null);
        }

        public void UpdateData(bool immediately = true)
        {
            if (immediately)
            {
                this.willUpdateData |= 3; // 0011
                this.InternalUpdateData();
            }
            else
            {
                if (this.willUpdateData == 0 && this.IsActive())
                {
                    if (this.delayedUpdateCoroutine != null)
                    {
                        this.StopCoroutine(this.delayedUpdateCoroutine);
                    }

                    this.delayedUpdateCoroutine = this.StartCoroutine(this.DelayUpdateData());
                }

                this.willUpdateData |= 3;
            }
        }

        public void UpdateDataIncrementally(bool immediately = true)
        {
            if (immediately)
            {
                this.willUpdateData |= 1; // 0001
                this.InternalUpdateData();
            }
            else
            {
                if (this.willUpdateData == 0)
                {
                    this.StartCoroutine(this.DelayUpdateData());
                }

                this.willUpdateData |= 1;
            }
        }

        public void ScrollTo(int index)
        {
            this.InternalScrollTo(index);
        }

        protected void EnsureItemRect(int index)
        {
            if (index < 0 || index >= this.managedItems.Count)
            {
                Debug.LogError(
                    $"[{nameof(this.EnsureItemRect)}]: Index {index} is out of bounds. managedItems.Count: {this.managedItems.Count}");
                return;
            }

            if (!this.managedItems[index].rectDirty)
            {
                // 已经是干净的了
                return;
            }

            ScrollItemWithRect firstItem = this.managedItems[0];
            if (firstItem.rectDirty)
            {
                Vector2 firstSize = this.GetItemSize(0);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (firstSize.x <= 0 || firstSize.y <= 0)
                {
                    Debug.LogWarning($"First item's size is {firstSize}. Both x and y should be greater than 0");
                }
#endif
                var contentHeight = this.content.sizeDelta.y;

                firstItem.rect = this.layoutType == ItemLayoutType.VerticalBottomUp
                    ? CreateWithLeftTopAndSize(
                        new Vector2(this.padding.left, contentHeight - this.padding.top), firstSize)
                    : CreateWithLeftTopAndSize(new Vector2(this.padding.left, -this.padding.top), firstSize);

                firstItem.rectDirty = false;
                return;
            }

            // 当前item之前的最近的已更新的rect
            var nearestClean = 0;
            for (var i = index; i >= 0; --i)
            {
                if (!this.managedItems[i].rectDirty)
                {
                    nearestClean = i;
                    break;
                }
            }

            // Re-check
            if (!this.managedItems[index].rectDirty)
            {
                return;
            }

            // 需要更新 从 nearestClean 到 index 的尺寸
            Rect nearestCleanRect = this.managedItems[nearestClean].rect;
            Vector2 curPos = GetLeftTop(nearestCleanRect);
            Vector2 size = nearestCleanRect.size;
            this.MovePos(ref curPos, size);

            for (var i = nearestClean + 1; i <= index; i++)
            {
                Vector2 curSize = this.GetItemSize(i);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (curSize.x <= 0 || curSize.y <= 0)
                {
                    Debug.LogWarning($"item {i} has size {curSize}, both x and y should be greater than 0");
                }
#endif
                this.managedItems[i].rect = CreateWithLeftTopAndSize(curPos, curSize);
                this.managedItems[i].rectDirty = false;
                this.MovePos(ref curPos, curSize);
            }

            // var range = new Vector2(Mathf.Abs(curPos.x), Mathf.Abs(curPos.y));
            // switch (this.layoutType)
            // {
            //     case ItemLayoutType.VerticalThenHorizontal:
            //         range.x += size.x;
            //         range.y = this.refRect.height;
            //         break;
            //     case ItemLayoutType.HorizontalThenVertical:
            //         range.x = this.refRect.width;
            //         if (curPos.x != 0)
            //         {
            //             range.y += size.y;
            //         }
            //
            //         break;
            //     default:
            //         break;
            // }
            //
            // this.content.sizeDelta = range;
        }

        protected Rect GetItemLocalRect(int index)
        {
            if (index >= 0 && index < this.dataCount)
            {
                this.EnsureItemRect(index);
                return this.managedItems[index].rect;
            }

            return (Rect)default;
        }

        private RectTransform GetCriticalItem(byte type)
        {
            var index = this.criticalItemIndex[type];
            if (index >= 0 && index < this.dataCount)
            {
                return this.managedItems[index].item;
            }

            return null;
        }

        private void UpdateCriticalItems()
        {
            var dirty = true;

            while (dirty)
            {
                dirty = false;

                for (byte i = CriticalItemType.UpToHide; i <= CriticalItemType.DownToShow; i++)
                {
                    if (i <= CriticalItemType.DownToHide)
                    {
                        // 隐藏离开可见区域的item
                        dirty = dirty || this.CheckAndHideItem(i);
                    }
                    else
                    {
                        // 显示进入可见区域的item
                        dirty = dirty || this.CheckAndShowItem(i);
                    }
                }
            }
        }

        private bool CheckAndHideItem(byte criticalItemType)
        {
            RectTransform item = this.GetCriticalItem(criticalItemType);
            var criticalIndex = this.criticalItemIndex[criticalItemType];
            if (item != null && !this.ShouldItemSeenAtIndex(criticalIndex))
            {
                this.RecycleOldItem(item);
                this.managedItems[criticalIndex].item = null;

                if (criticalItemType == CriticalItemType.UpToHide)
                {
                    // 最上隐藏了一个
                    this.criticalItemIndex[criticalItemType + 2] =
                        Mathf.Max(criticalIndex, this.criticalItemIndex[criticalItemType + 2]);
                    this.criticalItemIndex[criticalItemType]++;
                }
                else
                {
                    // 最下隐藏了一个
                    this.criticalItemIndex[criticalItemType + 2] =
                        Mathf.Min(criticalIndex, this.criticalItemIndex[criticalItemType + 2]);
                    this.criticalItemIndex[criticalItemType]--;
                }

                this.criticalItemIndex[criticalItemType] =
                    Mathf.Clamp(this.criticalItemIndex[criticalItemType], 0, this.dataCount - 1);

                if (this.criticalItemIndex[CriticalItemType.UpToHide] >
                    this.criticalItemIndex[CriticalItemType.DownToHide])
                {
                    // 偶然的情况 拖拽超出一屏
                    this.ResetCriticalItems();
                    return false;
                }

                return true;
            }

            return false;
        }

        private bool CheckAndShowItem(byte criticalItemType)
        {
            RectTransform item = this.GetCriticalItem(criticalItemType);
            var criticalIndex = this.criticalItemIndex[criticalItemType];

            if (item == null && this.ShouldItemSeenAtIndex(criticalIndex))
            {
                RectTransform newItem = this.GetNewItem(criticalIndex);
                this.OnGetItemForDataIndex(newItem, criticalIndex);
                this.managedItems[criticalIndex].item = newItem;

                if (criticalItemType == CriticalItemType.UpToShow)
                {
                    // 最上显示了一个
                    this.criticalItemIndex[criticalItemType - 2] =
                        Mathf.Min(criticalIndex, this.criticalItemIndex[criticalItemType - 2]);
                    this.criticalItemIndex[criticalItemType]--;
                }
                else
                {
                    // 最下显示了一个
                    this.criticalItemIndex[criticalItemType - 2] =
                        Mathf.Max(criticalIndex, this.criticalItemIndex[criticalItemType - 2]);
                    this.criticalItemIndex[criticalItemType]++;
                }

                this.criticalItemIndex[criticalItemType] =
                    Mathf.Clamp(this.criticalItemIndex[criticalItemType], 0, this.dataCount - 1);

                if (this.criticalItemIndex[CriticalItemType.UpToShow] >=
                    this.criticalItemIndex[CriticalItemType.DownToShow])
                {
                    // 偶然的情况 拖拽超出一屏
                    this.ResetCriticalItems();
                    return false;
                }

                return true;
            }

            return false;
        }

        private void OnGetItemForDataIndex(RectTransform item, int index)
        {
            this.SetDataForItemAtIndex(item, index);
            item.transform.SetParent(this.content, false);
        }

        private void SetDataForItemAtIndex(RectTransform item, int index)
        {
            if (this.updateFunc != null)
            {
                try
                {
                    this.updateFunc(index, item);
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogException(e);
                }
            }

            this.SetPosForItemAtIndex(item, index);
        }

        private void SetPosForItemAtIndex(RectTransform item, int index)
        {
            this.EnsureItemRect(index);
            Rect r = this.managedItems[index].rect;
            item.localPosition = r.position;
            item.sizeDelta = r.size;
        }

        private RectTransform GetNewItem(int index)
        {
            RectTransform item;
            if (this.itemGetFunc != null)
            {
                try
                {
                    item = this.itemGetFunc(index);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    item = null;
                }
            }
            else
            {
                item = this.itemPool.Get();
            }

            if (!item)
            {
                return item;
            }

            item.anchorMin = this.layoutType == ItemLayoutType.VerticalBottomUp ? Vector2.zero : Vector2.up;
            item.anchorMax = this.layoutType == ItemLayoutType.VerticalBottomUp ? Vector2.zero : Vector2.up;
            item.pivot = Vector2.zero;

            return item;
        }

        // const int 代替 enum 减少 (int)和(CriticalItemType)转换
        protected static class CriticalItemType
        {
            public static byte UpToHide = 0;
            public static byte DownToHide = 1;
            public static byte UpToShow = 2;
            public static byte DownToShow = 3;
        }

        private class ScrollItemWithRect
        {
            // scroll item 身上的 RectTransform组件
            public RectTransform item;

            // scroll item 在scrollview中的位置
            public Rect rect;

            // rect 是否需要更新
            public bool rectDirty = true;
        }
    }
}
