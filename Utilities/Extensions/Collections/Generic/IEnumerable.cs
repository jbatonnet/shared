﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace System.Collections.Generic
{
    public static class IEnumerableExtensions
    {
        // Common
        public static IEnumerable<T> Concat<T>(this IEnumerable<T> me, T value)
        {
            foreach (T item in me)
                yield return item;

            yield return value;
        }
        public static IEnumerable<T> Except<T>(this IEnumerable<T> me, T value)
        {
            foreach (T item in me)
                if (!item.Equals(value))
                    yield return item;
        }
        public static T MinValue<T>(this IEnumerable<T> me, Func<T, float> predicate)
        {
            return me.OrderBy(predicate).FirstOrDefault();
        }
        public static T MaxValue<T>(this IEnumerable<T> me, Func<T, float> predicate)
        {
            return me.OrderBy(predicate).LastOrDefault();
        }
        public static IEnumerable<U> SelectMany<T, U>(this IEnumerable<T> me, Func<T, IEnumerable<U>> filter, U separator)
        {
            List<U> result = new List<U>();
            List<U> lastItems = new List<U>();

            for (int i = 0; i < me.Count(); i++)
            {
                List<U> items = filter(me.ElementAt(i)).ToList();

                if (lastItems.Count > 0 && items.Count > 0)
                    result.Add(separator);

                result.AddRange(items);
                lastItems = items;
            }

            return result;
        }
        public static IEnumerable<TItem> Distinct<TItem, TKey>(this IEnumerable<TItem> me, Func<TItem, TKey> selector, IEqualityComparer<TKey> comparer = null)
        {
            comparer = comparer ?? EqualityComparer<TKey>.Default;

            HashSet<TKey> keys = new HashSet<TKey>();

            foreach (TItem item in me)
            {
                TKey key = selector(item);

                if (keys.Contains(key, comparer))
                    continue;

                yield return item;
            }
        }
        public static string Join<T>(this IEnumerable<T> me, string separator = "")
        {
            return string.Join(separator, me);
        }
        public static string Join<T>(this IEnumerable<T> me, Func<T, string> stringifier, string separator = "")
        {
            return string.Join(separator, me.Select(stringifier));
        }

        // Dictionaries
        public static Dictionary<TKey, TValue> ToDictionary<TKey, TValue>(this IEnumerable<KeyValuePair<TKey, TValue>> me)
        {
            return me.ToDictionary(p => p.Key, p => p.Value);
        }

        // DateTime
        public static TimeSpan Sum<T>(this IEnumerable<T> enumerable, Func<T, TimeSpan> selector)
        {
            TimeSpan sum = TimeSpan.Zero;
            foreach (T item in enumerable)
                sum += selector(item);
            return sum;
        }
    }
}