using System;
using System.Collections.Generic;
using System.Linq;

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Database;
using Android.Graphics;
using Android.OS;
using Android.Runtime;
using Android.Support.Design.Widget;
using Android.Support.V4.Widget;
using Android.Support.V4.View;
using Android.Support.V7.App;
using Android.Support.V7.Widget;
using Android.Utilities;
using Android.Views;
using Android.Widget;

using Java.Lang;

using Fragment = Android.Support.V4.App.Fragment;
using Toolbar = Android.Support.V7.Widget.Toolbar;
using SearchView = Android.Support.V7.Widget.SearchView;

namespace Android.Utilities
{
    public abstract class TabFragment : Fragment
    {
        public abstract string Title { get; }

        protected virtual void OnGotFocus() { }
        protected virtual void OnLostFocus() { }

        public virtual void Refresh() { }
        public override void SetMenuVisibility(bool visible)
        {
            base.SetMenuVisibility(visible);

            /*if (visible)
                OnGotFocus();
            else
                OnLostFocus();*/
        }

        public override bool UserVisibleHint
        {
            get
            {
                return base.UserVisibleHint;
            }
            set
            {
                base.UserVisibleHint = value;

                if (value)
                    OnGotFocus();
                else
                    OnLostFocus();
            }
        }
    }
}