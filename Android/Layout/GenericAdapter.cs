using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Support.V4.App;
using Android.Support.V7.Widget;
using Android.Views;

namespace Android.Utilities
{
    public delegate void GenericResourceBinder(View v);

    public class GenericRecyclerViewHolder<T> : RecyclerView.ViewHolder
    {
        public Dictionary<int, View> Views { get; } = new Dictionary<int, View>();

        public GenericRecyclerViewHolder(View view, IEnumerable<int> resourceIds) : base(view)
        {
            foreach (int resourceId in resourceIds)
                Views.Add(resourceId, view.FindViewById(resourceId));
        }
    }
    public class GenericRecyclerViewAdapter<T> : RecyclerView.Adapter, View.IOnClickListener where T : class
    {
        public override int ItemCount
        {
            get
            {
                return items.Length;
            }
        }

        private int layoutId;
        private Dictionary<int, GenericResourceBinder> resourceBinders = new Dictionary<int, GenericResourceBinder>();
        private Dictionary<T, GenericRecyclerViewHolder<T>> itemViewHolders = new Dictionary<T, GenericRecyclerViewHolder<T>>();

        private T[] items;

        public GenericRecyclerViewAdapter(T[] items, int layoutId, Dictionary<int, GenericResourceBinder> resourceBinders)
        {
            this.items = items;
            this.layoutId = layoutId;
        }

        public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
        {
            View view = LayoutInflater.From(parent.Context).Inflate(layoutId, parent, false);

            view.SetOnClickListener(this);

            return new GenericRecyclerViewHolder<T>(view, resourceBinders.Keys);
        }
        public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
        {
            GenericRecyclerViewHolder<T> viewHolder = holder as GenericRecyclerViewHolder<T>;
            T item = items[position];
            itemViewHolders[item] = viewHolder;

            foreach (var pair in resourceBinders)
            {
                View view = viewHolder.Views[pair.Key];
                pair.Value(view);
            }
        }

        public void Refresh(T[] items)
        {
            this.items = items;
            NotifyDataSetChanged();
        }

        void View.IOnClickListener.OnClick(View view)
        {
            GenericRecyclerViewHolder<T> viewHolder = itemViewHolders.Values.First(vh => vh.ItemView == view);
            T item = items[viewHolder.AdapterPosition];
        }
    }

    public class GenericAdapter<T> where T : class
    {
        public IEnumerable<T> Items
        {
            get
            {
                return rawItems;
            }
            set
            {
                rawItems = value?.ToArray();

                ApplyFilter(false);
                ApplySorting(false);

                Refresh();
            }
        }
        public Func<T, bool> Filter
        {
            get
            {
                return filter;
            }
            set
            {
                filter = value;

                ApplyFilter(false);

                Refresh();
            }
        }
        public Comparer<T> Sort
        {
            get
            {
                return sort;
            }
            set
            {
                sort = value;

                ApplySorting(false);

                Refresh();
            }
        }

        private int layoutId;
        private Dictionary<int, GenericResourceBinder> resourceBinders = new Dictionary<int, GenericResourceBinder>();

        private T[] rawItems;
        private T[] filteredItems;

        private Func<T, bool> filter;
        private Comparer<T> sort;

        public GenericAdapter(IEnumerable<T> items, int layoutId)
        {
            Items = items;

            this.layoutId = layoutId;
        }

        private void ApplyFilter(bool refresh = true)
        {
            if (filter == null)
                filteredItems = rawItems;
            else
                filteredItems = rawItems.Where(filter).ToArray();

            if (refresh)
                Refresh();
        }
        private void ApplySorting(bool refresh = true)
        {
            if (sort != null)
                Array.Sort(filteredItems, sort);

            if (refresh)
                Refresh();
        }

        private void Refresh()
        {
            foreach (GenericRecyclerViewAdapter<T> adapter in recyclerViewAdapters)
                adapter.Refresh(filteredItems);
        }

        private List<GenericRecyclerViewAdapter<T>> recyclerViewAdapters = new List<GenericRecyclerViewAdapter<T>>();
        public static implicit operator RecyclerView.Adapter(GenericAdapter<T> me)
        {
            GenericRecyclerViewAdapter<T> adapter = new GenericRecyclerViewAdapter<T>(me.filteredItems, me.layoutId, me.resourceBinders);
            me.recyclerViewAdapters.Add(adapter);
            return adapter;
        }
    }
}