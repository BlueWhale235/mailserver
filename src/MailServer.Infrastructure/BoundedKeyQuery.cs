using System.Linq.Expressions;

namespace MailServer.Infrastructure;

public static class BoundedKeyQuery
{
    // The MySQL provider does not reliably map collections of converted GUIDs.
    // Bounded equality parameters retain the property mapping on both providers.
    internal static IQueryable<T> WhereIdIn<T>(this IQueryable<T> query,
        Expression<Func<T, long>> key, IEnumerable<long> ids) => WhereKeyIn(query, key, ids);

    public static IQueryable<T> WhereStringIn<T>(this IQueryable<T> query,
        Expression<Func<T, string>> key, IEnumerable<string> values) => WhereKeyIn(query, key, values);

    private static IQueryable<T> WhereKeyIn<T,TKey>(IQueryable<T> query,
        Expression<Func<T, TKey>> key, IEnumerable<TKey> ids)
    {
        Expression predicate = Expression.Constant(false);
        foreach (var id in ids)
        {
            Expression<Func<TKey>> value = () => id;
            predicate = Expression.OrElse(predicate, Expression.Equal(key.Body, value.Body));
        }
        return query.Where(Expression.Lambda<Func<T, bool>>(predicate, key.Parameters));
    }
}
