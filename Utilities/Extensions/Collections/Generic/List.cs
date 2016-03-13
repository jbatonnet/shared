using System;
using System.Collections.Generic;
using System.Text;

namespace System.Collections.Generic
{
    public static class ListExtensions
    {
        public static void AddRange<T>(this List<T> me, params T[] items)
        {
            me.AddRange(items);
        }
    }
}
