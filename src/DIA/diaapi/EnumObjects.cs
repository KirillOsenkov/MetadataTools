using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Collections;

namespace VisualStudioProvider.PDB.diaapi
{
    public class EnumObjects<T> : IEnumerable<T>, IEnumerator<T>
    {
        private Func<int> count;
        private Func<EnumItemValue<T>, bool> next;
        private Func<int, T> item;
        private int currentIndex;
        private Action reset;
        private int enumCount;
        private T currentItem;

        public EnumObjects()
        {
        }

        public EnumObjects(Func<int> count, Func<EnumItemValue<T>, bool> next, Func<int, T> item, Action reset)
        {
            this.count = count;
            this.next = next;
            this.item = item;
            this.reset = reset;
        }

        public IEnumerator<T> GetEnumerator()
        {
            if (count != null)
            {
                enumCount = count();
                Reset();
            }

            return this;
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            if (count != null)
            {
                enumCount = count();
                Reset();
            }

            return this;
        }

        public T Current
        {
            get 
            {
                return currentItem;
            }
        }

        public void Dispose()
        {
        }

        object IEnumerator.Current
        {
            get 
            {
                return currentItem;
            }
        }

        public bool MoveNext()
        {
            bool result = false;

            if (next != null)
            {
                var itemValue = new EnumItemValue<T>();

                currentIndex++;
                result = next(itemValue);

                currentItem = itemValue.Value;
            }

            return result;
        }

        public void Reset()
        {
            enumCount = 0;
            currentIndex = -1;

            if (reset != null)
            {
                reset();
            }
        }

        public class EnumItemValue<T>
        {
            public T Value { get; set; }
            public object InternalValue { get; set; }
        }
    }
}
