namespace AillieoUtils
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using UnityEngine;
    using UnityEngine.UI;

    public partial class ScrollView
    {
        protected virtual void InternalScrollTo(int index)
        {
            if (this.dataCount == 0)
            {
                return;
            }

            if (index < 0)
            {
                index += this.dataCount;
            }

            index = Mathf.Clamp(index, 0, this.dataCount - 1);
            this.EnsureItemRect(index);
            Rect r = this.managedItems[index].rect;
            var dir = (int)this.layoutType & FlagScrollDirection;
            if (dir == 1)
            {
                // vertical
                var value = 1 - (-r.yMax / (this.content.sizeDelta.y - this.refRect.height));
                value = Mathf.Clamp01(value);
                this.SetNormalizedPosition(value, 1);
            }
            else
            {
                // horizontal
                var value = r.xMin / (this.content.sizeDelta.x - this.refRect.width);
                value = Mathf.Clamp01(value);
                this.SetNormalizedPosition(value, 0);
            }
        }

        /// <summary>
        /// Check if the data count has changed. If it has changed, update <see cref="dataCount"/> and
        /// <see cref="managedItems"/> accordingly. If the data count has not changed, do nothing.
        /// <br/>
        /// This function is called by the base class whenever the data count might have changed, e.g. when
        /// <see cref="itemCountFunc"/> is set.
        /// </summary>
        protected virtual void CheckDataCountChange()
        {
            var newDataCount = 0;
            newDataCount = HasItemCountFunc(this.itemCountFunc) ? this.itemCountFunc() : this.poolSize;

            var keepOldItems = (this.willUpdateData & 2) == 0;

            this.UpdateManagedItemsList(newDataCount, keepOldItems);

            this.dataCount = newDataCount;
        }

        private IEnumerator DelayUpdateData()
        {
            yield return new WaitForEndOfFrame();

            this.InternalUpdateData();

            this.delayedUpdateCoroutine = null;
        }

        private void InternalUpdateData()
        {
            if (!this.IsActive())
            {
                this.willUpdateData |= 3;
                return;
            }

            if (!this.initialized)
            {
                this.InitScrollView();
            }

            this.CheckDataCountChange();
            this.CalculateContentSizeAndItemPos();
            this.ResetCriticalItems();

            this.willUpdateData = 0;

            if (this.layoutType == ItemLayoutType.VerticalBottomUp)
            {
                this.verticalNormalizedPosition = 0f;
            }
        }

        private void InitScrollView()
        {
            if (!Application.isPlaying)
            {
                return;
            }

            this.initialized = true;

            // 根据设置来控制原ScrollRect的滚动方向
            var dir = (int)this.layoutType & FlagScrollDirection;
            this.vertical = dir == 1;
            this.horizontal = dir == 0;

            this.content.pivot = this.layoutType == ItemLayoutType.VerticalBottomUp ? Vector2.zero : Vector2.up;
            this.content.anchorMin = this.layoutType == ItemLayoutType.VerticalBottomUp ? Vector2.zero : Vector2.up;
            this.content.anchorMax = this.layoutType == ItemLayoutType.VerticalBottomUp ? Vector2.zero : Vector2.up;
            this.content.anchoredPosition = Vector2.zero;

            this.InitPool();
            this.UpdateRefRect();
        }

        private void InitPool()
        {
            if (this.itemGetFunc != null || this.itemRecycleFunc != null)
            {
                return;
            }

            if (this.itemPool != null)
            {
                return;
            }

            var poolNode = new GameObject("POOL");
            poolNode.SetActive(false);
            poolNode.transform.SetParent(this.transform, false);
            this.itemPool = new SimpleObjPool<RectTransform>(
                this.poolSize,
                (RectTransform item) =>
                {
                    item.transform.SetParent(poolNode.transform, false);
                },
                () =>
                {
                    GameObject itemObj = Instantiate(this.itemTemplate.gameObject, poolNode.transform, false);
                    RectTransform item = itemObj.GetComponent<RectTransform>();

                    item.anchorMin = this.layoutType == ItemLayoutType.VerticalBottomUp ? Vector2.zero : Vector2.up;
                    item.anchorMax = this.layoutType == ItemLayoutType.VerticalBottomUp ? Vector2.zero : Vector2.up;
                    item.pivot = Vector2.zero;

                    itemObj.SetActive(true);
                    return item;
                },
                (RectTransform item) =>
                {
                    if (!this.applicationIsQuitting)
                    {
                        item.transform.SetParent(null, false);
                        Destroy(item.gameObject);
                    }
                });
        }

        // refRect是在Content节点下的 viewport的 rect
        private void UpdateRefRect()
        {
            if (!CanvasUpdateRegistry.IsRebuildingLayout())
            {
                Canvas.ForceUpdateCanvases();
            }

            this.viewRect.GetWorldCorners(viewWorldCorners);
            this.rectCorners[0] = this.content.transform.InverseTransformPoint(viewWorldCorners[0]);
            this.rectCorners[1] = this.content.transform.InverseTransformPoint(viewWorldCorners[2]);

            var size = this.rectCorners[1] - this.rectCorners[0];
            var pos = (Vector2)this.rectCorners[0];

            this.refRect = new Rect(pos, size);
        }

        private void CalculateContentSizeAndItemPos()
        {
            Vector2 curPos;
            var totalHeightForBottomUp = 0f;

            if (this.layoutType == ItemLayoutType.VerticalBottomUp)
            {
                totalHeightForBottomUp = this.padding.top + this.padding.bottom;
                if (this.dataCount > 0)
                {
                    for (var i = 0; i < this.dataCount; i++)
                    {
                        totalHeightForBottomUp += this.GetItemSize(i).y;
                    }

                    totalHeightForBottomUp += Mathf.Max(0, this.dataCount - 1) * this.spacing.y;
                }

                // otalHeight = Mathf.Max(0, totalHeight);
                curPos = new Vector2(this.padding.left, totalHeightForBottomUp - this.padding.top);
            }
            else
            {
                curPos = new Vector2(this.padding.left, -this.padding.bottom);
            }

            var maxX = float.MinValue;
            var minY = float.MaxValue;

            for (var i = 0; i < this.dataCount; i++)
            {
                var size = this.GetItemSize(i);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (size.x <= 0 || size.y <= 0)
                {
                    Debug.LogWarning($"item {i} size is {size}, both x and y should be greater than 0");
                }
#endif

                this.managedItems[i].rect = CreateWithLeftTopAndSize(curPos, size);
                minY = Mathf.Min(minY, this.managedItems[i].rect.yMin);

                this.managedItems[i].rectDirty = false;

                // maxX = Mathf.Max(maxX, curPos.x + size.x);
                maxX = Mathf.Max(maxX, this.managedItems[i].rect.xMax);

                this.MovePos(ref curPos, size);
            }

            var contentWidth = 0f;
            var contentHeight = 0f;

            switch (this.layoutType)
            {
                case ItemLayoutType.Vertical:
                case ItemLayoutType.HorizontalThenVertical:
                    contentWidth = this.refRect.width;
                    contentHeight = -minY + this.padding.bottom;
                    break;
                case ItemLayoutType.Horizontal:
                case ItemLayoutType.VerticalThenHorizontal:
                    contentWidth = maxX + this.padding.right;
                    contentHeight = this.refRect.height;
                    break;
                case ItemLayoutType.VerticalBottomUp:
                    contentWidth = this.refRect.width;
                    contentHeight = totalHeightForBottomUp;
                    break;
            }

            contentWidth = Mathf.Max(contentWidth, 0);
            contentHeight = Mathf.Max(contentHeight, 0);

            this.content.sizeDelta = new Vector2(contentWidth, contentHeight);
        }

        private void ResetCriticalItems()
        {
            int firstIndex = -1, lastIndex = -1;

            for (var i = 0; i < this.dataCount; i++)
            {
                var hasItem = this.managedItems[i].item;
                var shouldShow = this.ShouldItemSeenAtIndex(i);

                if (shouldShow)
                {
                    if (firstIndex == -1)
                    {
                        firstIndex = i;
                    }

                    lastIndex = i;
                }

                // Item exists and should be shown
                if (hasItem && shouldShow)
                {
                    // 应显示且已显示
                    this.SetDataForItemAtIndex(this.managedItems[i].item, i);
                    continue;
                }

                // Item doesn't exist and shouldn't be shown
                if (hasItem == shouldShow)
                {
                    // 不应显示且未显示
                    // if (firstIndex != -1)
                    // {
                    //     // 已经遍历完所有要显示的了 后边的先跳过
                    //     break;
                    // }
                    continue;
                }

                // Item exists but shouldn't be shown
                if (hasItem && !shouldShow)
                {
                    // 不该显示 但是有
                    this.RecycleOldItem(this.managedItems[i].item);
                    this.managedItems[i].item = null;
                    continue;
                }

                // Item doesn't exist but should be shown
                if (shouldShow && !hasItem)
                {
                    // 需要显示 但是没有
                    RectTransform item = this.GetNewItem(i);
                    this.OnGetItemForDataIndex(item, i);
                    this.managedItems[i].item = item;
                    continue;
                }
            }

            // content.localPosition = Vector2.zero;
            this.criticalItemIndex[CriticalItemType.UpToHide] = firstIndex;
            this.criticalItemIndex[CriticalItemType.DownToHide] = lastIndex;
            this.criticalItemIndex[CriticalItemType.UpToShow] = Mathf.Max(firstIndex - 1, 0);
            this.criticalItemIndex[CriticalItemType.DownToShow] = Mathf.Min(lastIndex + 1, this.dataCount - 1);
        }

        private void MovePos(ref Vector2 pos, Vector2 size)
        {
            // 注意 所有的rect都是左下角为基准
            switch (this.layoutType)
            {
                case ItemLayoutType.Vertical:
                case ItemLayoutType.VerticalBottomUp:
                    // 垂直方向 向下移动
                    pos.y -= size.y + this.spacing.y;
                    break;
                case ItemLayoutType.Horizontal:
                    // 水平方向 向右移动
                    pos.x += size.x + this.spacing.x;
                    break;
                case ItemLayoutType.VerticalThenHorizontal:
                    pos.y -= size.y + this.spacing.y;
                    if (pos.y - size.y < -this.refRect.height + this.padding.bottom)
                    {
                        pos.y = -this.padding.top;
                        pos.x += size.x + this.spacing.x;
                    }

                    break;
                case ItemLayoutType.HorizontalThenVertical:
                    pos.x += size.x + this.spacing.x;
                    if (pos.x + size.x > this.refRect.width - this.padding.right)
                    {
                        pos.x = this.padding.left;
                        pos.y -= size.y + this.spacing.y;
                    }

                    break;

                    // case ItemLayoutType.VerticalBottomUp:
                    //     pos.y += size.y + this.spacing.y;
                    //     break;
            }
        }

        private void RecycleOldItem(RectTransform item)
        {
            if (this.itemRecycleFunc != null)
            {
                try
                {
                    this.itemRecycleFunc(item);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
            else
            {
                this.itemPool.Recycle(item);
            }
        }

        // ---- Helpers ----
        private Vector2 GetItemSize(int index)
        {
            if (index >= 0 && index <= this.dataCount)
            {
                if (this.itemSizeFunc != null)
                {
                    try
                    {
                        return this.itemSizeFunc(index);
                    }
                    catch (Exception e)
                    {
                        UnityEngine.Debug.LogException(e);
                    }
                }
            }

            return this.defaultItemSize;
        }

        private bool ShouldItemSeenAtIndex(int index)
        {
            if (index < 0 || index >= this.dataCount)
            {
                return false;
            }

            this.EnsureItemRect(index);
            return new Rect(this.refRect.position - this.content.anchoredPosition, this.refRect.size).Overlaps(
                this.managedItems[index].rect);
        }

        private void UpdateManagedItemsList(int newDataCount, bool keepOldItems)
        {
            if (this.managedItems.Count < newDataCount)
            {
                // Need to add new items
                this.HandleItemIncrease(newDataCount, keepOldItems);
            }
            else if (this.managedItems.Count > newDataCount)
            {
                this.HandleItemDecrease(newDataCount, keepOldItems);
            }
            else
            {
                // No data changes.
                NeedResetAllRects(keepOldItems, this.managedItems);
            }
        }

        private void HandleItemIncrease(int newDataCount, bool keepOldItems)
        {
            NeedResetAllRects(keepOldItems, this.managedItems);

            while (this.managedItems.Count < newDataCount)
            {
                this.managedItems.Add(new ScrollItemWithRect());
            }
        }

        private void HandleItemDecrease(int newDataCount, bool keepOldItems)
        {
            // 减少 保留空位 避免GC
            for (int i = 0; i < newDataCount; ++i)
            {
                if (!keepOldItems || i == newDataCount - 1)
                {
                    this.managedItems[i].rectDirty = true;
                }
            }

            // 超出部分 清理回收item
            for (int i = this.managedItems.Count - 1; i >= newDataCount; --i)
            {
                if (this.managedItems[i].item)
                {
                    this.RecycleOldItem(this.managedItems[i].item);
                    this.managedItems[i].item = null;
                }

                this.managedItems[i].rectDirty = true;
            }
        }

        private static void NeedResetAllRects(bool keepOldItems, List<ScrollItemWithRect> managedItems)
        {
            if (keepOldItems)
            {
                return;
            }

            foreach (ScrollItemWithRect itemWithRect in managedItems)
            {
                // 重置所有rect
                itemWithRect.rectDirty = true;
            }
        }

        private static Vector2 GetLeftTop(Rect rect)
        {
            Vector2 ret = rect.position;
            ret.y += rect.size.y;
            return ret;
        }

        private static Rect CreateWithLeftTopAndSize(Vector2 leftTop, Vector2 size)
        {
            Vector2 leftBottom = leftTop - new Vector2(0, size.y);
            return new Rect(leftBottom, size);
        }

        // ---- Rules ----
        private static bool HasItemCountFunc(Func<int> itemCountFunc)
        {
            if (itemCountFunc != null)
            {
                return true;
            }

            Debug.LogWarning("No item count delegate is set. Using the object pool size instead.");
            return false;
        }
    }
}
