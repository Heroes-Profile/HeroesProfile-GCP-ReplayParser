using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;

namespace HeroesProfile_GCP_ReplayParser
{
    public static class Extensions
    {
        /// <summary>
        /// Executes specified delegate on all members of the collection
        /// </summary>
        public static void Map<T>(this IEnumerable<T> src, Action<T> action)
        {
            src.Select(q => { action(q); return 0; }).Count();
        }
    }
}