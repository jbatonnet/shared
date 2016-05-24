using Android.Support.V4.App;

namespace Android.Utilities
{
    public abstract class TabFragment : Fragment
    {
        public abstract string Title { get; }

        protected virtual void OnGotFocus() { }
        protected virtual void OnLostFocus() { }

        public virtual void Refresh() { }

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