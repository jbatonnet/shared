using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Support.V4.App;
using Android.Views;
using Android.Widget;
using Java.Lang;

namespace Android.Utilities
{
    public class TabFragmentsAdapter : FragmentPagerAdapter
    {
        public override int Count
        {
            get
            {
                return fragments.Length;
            }
        }

        private TabFragment[] fragments;

        public TabFragmentsAdapter(FragmentManager fragmentManager, params TabFragment[] fragments) : base(fragmentManager)
        {
            fragments = fragments;
        }

        public override Fragment GetItem(int position)
        {
            return fragments[position];
        }
        public override ICharSequence GetPageTitleFormatted(int position)
        {
            return fragments[position].Title;
        }
    }
}