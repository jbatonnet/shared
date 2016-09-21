using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace System
{
    public class Association<TLeft, TRight>
    {
        public IEnumerable<TLeft> Left
        {
            get
            {
                return leftToRight.Keys;
            }
        }
        public IEnumerable<TRight> Right
        {
            get
            {
                return rightToLeft.Keys;
            }
        }

        public TRight this[TLeft left]
        {
            get
            {
                return leftToRight[left];
            }
            set
            {
                TRight oldRight;

                if (leftToRight.TryGetValue(left, out oldRight))
                    rightToLeft.Remove(oldRight);

                leftToRight[left] = value;
                rightToLeft[value] = left;
            }
        }
        public TLeft this[TRight right]
        {
            get
            {
                return rightToLeft[right];
            }
            set
            {
                TLeft oldLeft;

                if (rightToLeft.TryGetValue(right, out oldLeft))
                    leftToRight.Remove(oldLeft);

                rightToLeft[right] = value;
                leftToRight[value] = right;
            }
        }

        private Dictionary<TLeft, TRight> leftToRight = new Dictionary<TLeft, TRight>();
        private Dictionary<TRight, TLeft> rightToLeft = new Dictionary<TRight, TLeft>();

        public void Add(TLeft left, TRight right)
        {
            if (leftToRight.ContainsKey(left))
                throw new NotSupportedException();

            leftToRight[left] = right;
            rightToLeft[right] = left;
        }

        public bool TryGetLeft(TRight right, out TLeft left)
        {
            return rightToLeft.TryGetValue(right, out left);
        }
        public bool TryGetRight(TLeft left, out TRight right)
        {
            return leftToRight.TryGetValue(left, out right);
        }
    }
}