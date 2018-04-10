using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Security;

namespace Castle.DynamicLinqQueryBuilder
{
    /// <summary>
    /// Generic IQueryable filter implementation.  Based upon configuration of FilterRules 
    /// mapping to the data source.  When applied, acts as an advanced filter mechanism.
    /// </summary>
    public static class QueryBuilder
    {
        /// <summary>
        /// Gets or sets a value indicating whether incoming dates in the filter should be parsed as UTC.
        /// </summary>
        /// <value>
        ///   <c>true</c> if [parse dates as UTC]; otherwise, <c>false</c>.
        /// </value>
        public static bool ParseDatesAsUtc { get; set; }

        /// <summary>
        /// Gets the filtered collection after applying the provided filter rules.
        /// </summary>
        /// <typeparam name="T">The generic type.</typeparam>
        /// <param name="queryable">The queryable.</param>
        /// <param name="filterRule">The filter rule.</param>
        /// <param name="useIndexedProperty">Whether or not to use indexed property</param>
        /// <param name="indexedPropertyName">The indexable property to use</param>
        /// <returns>Filtered IQueryable</returns>
        public static IQueryable<T> BuildQuery<T>(this IEnumerable<T> queryable, FilterRule filterRule, bool useIndexedProperty = false, string indexedPropertyName = null)
        {
            string parsedQuery;
            return BuildQuery(queryable.AsQueryable(), filterRule, out parsedQuery, useIndexedProperty, indexedPropertyName);
        }

        /// <summary>
        /// Gets the filtered collection after applying the provided filter rules.
        /// </summary>
        /// <typeparam name="T">The generic type.</typeparam>
        /// <param name="queryable">The queryable.</param>
        /// <param name="filterRule">The filter rule.</param>
        /// <param name="useIndexedProperty">Whether or not to use indexed property</param>
        /// <param name="indexedPropertyName">The indexable property to use</param>
        /// <returns>Filtered IQueryable</returns>
        public static IQueryable<T> BuildQuery<T>(this IList<T> queryable, FilterRule filterRule, bool useIndexedProperty = false, string indexedPropertyName = null)
        {
            string parsedQuery;
            return BuildQuery(queryable.AsQueryable(), filterRule, out parsedQuery, useIndexedProperty, indexedPropertyName);
        }

        /// <summary>
        /// Gets the filtered collection after applying the provided filter rules.
        /// </summary>
        /// <typeparam name="T">The generic type.</typeparam>
        /// <param name="queryable">The queryable.</param>
        /// <param name="filterRule">The filter rule.</param>
        /// <param name="useIndexedProperty">Whether or not to use indexed property</param>
        /// <param name="indexedPropertyName">The indexable property to use</param>
        /// <returns>Filtered IQueryable</returns>
        public static IQueryable<T> BuildQuery<T>(this IQueryable<T> queryable, FilterRule filterRule, bool useIndexedProperty = false, string indexedPropertyName = null)
        {
            string parsedQuery;
            return BuildQuery(queryable, filterRule, out parsedQuery, useIndexedProperty, indexedPropertyName);
        }

        /// <summary>
        /// Gets the filtered collection after applying the provided filter rules. 
        /// Returns the string representation for diagnostic purposes.
        /// </summary>
        /// <typeparam name="T">The generic type.</typeparam>
        /// <param name="queryable">The queryable.</param>
        /// <param name="filterRule">The filter rule.</param>
        /// <param name="parsedQuery">The parsed query.</param>
        /// <param name="useIndexedProperty">Whether or not to use indexed property</param>
        /// <param name="indexedPropertyName">The indexable property to use</param>
        /// <returns>Filtered IQueryable.</returns>
        public static IQueryable<T> BuildQuery<T>(this IQueryable<T> queryable, FilterRule filterRule, out string parsedQuery, bool useIndexedProperty = false, string indexedPropertyName = null)
        {
            if (filterRule == null)
            {
                parsedQuery = "";
                return queryable;
            }

            var pe = Expression.Parameter(typeof(T), "item");

            var expressionTree = BuildExpressionTree(pe, filterRule, useIndexedProperty, indexedPropertyName);
            if (expressionTree == null)
            {
                parsedQuery = "";
                return queryable;
            }

            parsedQuery = expressionTree.ToString();

            var whereCallExpression = Expression.Call(
                typeof(Queryable),
                "Where",
                new[] { queryable.ElementType },
                queryable.Expression,
                Expression.Lambda<Func<T, bool>>(expressionTree, pe));

            var filteredResults = queryable.Provider.CreateQuery<T>(whereCallExpression);

            return filteredResults;

        }       

        private static Expression BuildExpressionTree(ParameterExpression pe, FilterRule rule, bool useIndexedProperty = false, string indexedPropertyName = null)
        {            
            if (rule.Rules != null && rule.Rules.Any())
            {                
                if (!string.IsNullOrEmpty(rule.Field))
                {
                    var nestedProperty = getExpressionForNestedProperty(pe, rule.Field);
                    if (IsGenericList(nestedProperty.Type))
                    {
                        var collectionItemType = nestedProperty.Type.GetGenericArguments().Single(); // this gives us the type of the objects that the collection holds. e.g. CR.CRTasks (CRTasks = ICollection<CRTask>), then collectionItemType would equal CRTask
                        var nestedPropertyParameterExpression = Expression.Parameter(collectionItemType, "np");   
                        var nestedPropertyExpressions = rule.Rules
                            .Select(childRule => BuildExpressionTree(nestedPropertyParameterExpression, childRule, useIndexedProperty, indexedPropertyName))
                            .Where(expression => expression != null)
                            .ToList();

                        var innerExpressionTree = nestedPropertyExpressions.First();
                        for (int i = 1; i < nestedPropertyExpressions.Count; i++)
                        {
                            innerExpressionTree = rule.Condition.ToLower() == "or"
                                ? Expression.Or(innerExpressionTree, nestedPropertyExpressions[i])
                                : Expression.And(innerExpressionTree, nestedPropertyExpressions[i]);
                        }                        

                        return makeAnyExpression(collectionItemType, innerExpressionTree, nestedPropertyParameterExpression, nestedProperty);
                    }
                    else
                    {
                        new ArgumentException("QueryBuilder does not support nested rules defined for fields that are not collections.");
                    }
                }
                
                var expressions =
                    rule.Rules.Select(childRule => BuildExpressionTree(pe, childRule, useIndexedProperty, indexedPropertyName))
                        .Where(expression => expression != null)
                        .ToList();

                var expressionTree = expressions.First();

                for (int i = 1; i < expressions.Count; i++)
                {
                    expressionTree = rule.Condition.ToLower() == "or"
                        ? Expression.Or(expressionTree, expressions[i])
                        : Expression.And(expressionTree, expressions[i]);
                }

                return expressionTree;
            }
            if (rule.Field != null)
            {
                Type ruleType;

                switch (rule.Type)
                {
                    case "integer":
                        ruleType = typeof(int);
                        break;
                    case "double":
                        ruleType = typeof(double);
                        break;
                    case "string":
                        ruleType = typeof(string);
                        break;
                    case "date":
                    case "datetime":
                        ruleType = typeof(DateTime);
                        break;
                    case "boolean":
                        ruleType = typeof(bool);
                        break;
                    default:
                        throw new Exception($"Unexpected data type {rule.Type}");
                }

                Expression propertyExp = null;
                var propertyList = rule.Field.Split('.').ToList();
                if (useIndexedProperty)
                {
                    propertyExp = Expression.Property(pe, indexedPropertyName, Expression.Constant(rule.Field));
                }
                else
                {
                    propertyExp = GetLambda(pe, propertyList.ToArray(), 0, rule.Value, rule.Operator, ruleType);
                }

                return propertyExp;


            }
            return null;

        }

        private static Expression makeAnyExpression(Type collectionItemType, Expression lambdaBody, ParameterExpression lambdaParameter, Expression parentExpression)
        {
            var funcType = typeof(Func<,>).MakeGenericType(collectionItemType, typeof(bool));
            var lambda = Expression.Lambda(funcType, lambdaBody, lambdaParameter);
            var anyMethod = typeof(Enumerable).GetMethods()
                .Where(m => m.Name == "Any" && m.GetParameters().Length == 2).Single()
                .MakeGenericMethod(collectionItemType);

            var invokeMethod = Expression.Call(null, anyMethod, parentExpression, lambda);

            return invokeMethod;
        }

        private static MemberExpression getExpressionForNestedProperty(ParameterExpression pe, string dotNotationProperty)
        {
            var propertyList = dotNotationProperty.Split('.').ToList();
            MemberExpression nestedPropertyExpression = Expression.Property(pe, propertyList[0]);
            if (propertyList.Count > 1)
            {
                for (int i = 1; i < propertyList.Count; i++)
                {
                    nestedPropertyExpression = Expression.Property(nestedPropertyExpression, propertyList[i]);
                }
            }
            return nestedPropertyExpression;
        }

        private static Expression GetOperatorExpression(string op, Type type, string value, Expression propertyExp)
        {
            Expression expression = null;
            switch (op.ToLower())
            {
                case "in":
                    expression = In(type, value, propertyExp);

                    break;
                case "not_in":
                    expression = NotIn(type, value, propertyExp);
                    break;
                case "equal":
                    expression = Equals(type, value, propertyExp);
                    break;
                case "not_equal":
                    expression = NotEquals(type, value, propertyExp);
                    break;
                case "between":
                    expression = Between(type, value, propertyExp);
                    break;
                case "not_between":
                    expression = NotBetween(type, value, propertyExp);
                    break;
                case "less":
                    expression = LessThan(type, value, propertyExp);
                    break;
                case "less_or_equal":
                    expression = LessThanOrEqual(type, value, propertyExp);
                    break;
                case "greater":
                    expression = GreaterThan(type, value, propertyExp);
                    break;
                case "greater_or_equal":
                    expression = GreaterThanOrEqual(type, value, propertyExp);
                    break;
                case "begins_with":
                    expression = BeginsWith(type, value, propertyExp);
                    break;
                case "not_begins_with":
                    expression = NotBeginsWith(type, value, propertyExp);
                    break;
                case "contains":
                    expression = Contains(type, value, propertyExp);
                    break;
                case "not_contains":
                    expression = NotContains(type, value, propertyExp);
                    break;
                case "ends_with":
                    expression = EndsWith(type, value, propertyExp);
                    break;
                case "not_ends_with":
                    expression = NotEndsWith(type, value, propertyExp);
                    break;
                case "is_empty":
                    expression = IsEmpty(propertyExp);
                    break;
                case "is_not_empty":
                    expression = IsNotEmpty(propertyExp);
                    break;
                case "is_null":
                    expression = IsNull(propertyExp);
                    break;
                case "is_not_null":
                    expression = IsNotNull(propertyExp);
                    break;
                default:
                    throw new Exception($"Unknown expression operator: {op}");
            }

            return expression;
        }

        private static Expression GetLambda(Expression parent, string[] properties, int index, string value, string op, Type ruleType)
        {
            if (index < properties.Length)
            {
                var member = properties[index];

                if (IsGenericList(parent.Type))
                {
                    var collectionItemType = parent.Type.GetGenericArguments().Single(); // this gives us the type of the objects that the collection holds. e.g. CR.CRTasks (CRTasks = ICollection<CRTask>), then collectionItemType would equal CRTask
                    var lambdaParameter = Expression.Parameter(collectionItemType, "p"); // the parameter for the lambda expression
                    var lambdaBody = GetLambda(lambdaParameter, properties, index, value, op, ruleType); // Recursively build the inner lambda                    
                    return makeAnyExpression(collectionItemType, lambdaBody, lambdaParameter, parent);
                }

                var newParent = Expression.Property(parent, member);
                return GetLambda(newParent, properties, ++index, value, op, ruleType);
            }

            var exOut = GetOperatorExpression(op, ruleType, value, parent);
            return exOut;
        }


        private static List<ConstantExpression> GetConstants(Type type, string value, bool isCollection)
        {
            if (type == typeof(DateTime) && ParseDatesAsUtc)
            {
                DateTime tDate;
                if (isCollection)
                {
                    var vals =
                        value.Split(new[] { ",", "[", "]", "\r\n" }, StringSplitOptions.RemoveEmptyEntries)
                            .Where(p => !string.IsNullOrWhiteSpace(p))
                            .Select(
                                p =>
                                    DateTime.TryParse(p.Trim(), CultureInfo.InvariantCulture,
                                        DateTimeStyles.AdjustToUniversal, out tDate)
                                        ? (DateTime?)
                                            tDate
                                        : null).Select(p =>
                                            Expression.Constant(p, type));
                    return vals.ToList();
                }
                else
                {
                    return new List<ConstantExpression>()
                    {
                        Expression.Constant(DateTime.TryParse(value.Trim(), CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal, out tDate)
                            ? (DateTime?)
                                tDate
                            : null)
                    };
                }
            }
            else
            {
                if (isCollection)
                {
                    var tc = TypeDescriptor.GetConverter(type);
                    var vals =
                        value.Split(new[] { ",", "[", "]", "\r\n" }, StringSplitOptions.RemoveEmptyEntries)
                            .Where(p => !string.IsNullOrWhiteSpace(p))
                            .Select(p => tc.ConvertFromString(p.Trim())).Select(p =>
                                Expression.Constant(p, type));
                    return vals.ToList();
                }
                else
                {
                    var tc = TypeDescriptor.GetConverter(type);
                    return new List<ConstantExpression>() { Expression.Constant(tc.ConvertFromString(value.Trim())) };
                }
            }



        }

        #region Expression Types

        private static Expression GetNullCheckExpression(Expression propertyExp)
        {
            var isNullable = !propertyExp.Type.IsValueType || Nullable.GetUnderlyingType(propertyExp.Type) != null;

            if (isNullable)
            {
                return Expression.NotEqual(propertyExp,
                    Expression.Constant(propertyExp.Type.GetDefaultValue(), propertyExp.Type));

            }
            return Expression.Equal(Expression.Constant(true, typeof(bool)),
                Expression.Constant(true, typeof(bool)));
        }



        private static Expression IsNull(Expression propertyExp)
        {
            var isNullable = !propertyExp.Type.IsValueType || Nullable.GetUnderlyingType(propertyExp.Type) != null;

            if (isNullable)
            {
                var someValue = Expression.Constant(null, propertyExp.Type);

                Expression exOut = Expression.Equal(propertyExp, someValue);

                return exOut;
            }
            return Expression.Equal(Expression.Constant(true, typeof(bool)),
                Expression.Constant(false, typeof(bool)));
        }

        private static Expression IsNotNull(Expression propertyExp)
        {
            return Expression.Not(IsNull(propertyExp));
        }

        private static Expression IsEmpty(Expression propertyExp)
        {
            var someValue = Expression.Constant(0, typeof(int));


            Expression exOut;

            if (IsGenericList(propertyExp.Type))
            {

                exOut = Expression.Property(propertyExp, propertyExp.Type.GetProperty("Count"));

                exOut = Expression.Equal(exOut, someValue);
            }
            else
            {
                var nullCheck = GetNullCheckExpression(propertyExp);

                exOut = Expression.Property(propertyExp, typeof(string).GetProperty("Length"));

                exOut = Expression.AndAlso(nullCheck, Expression.Equal(exOut, someValue));
            }

            return exOut;
        }

        private static Expression IsNotEmpty(Expression propertyExp)
        {
            return Expression.Not(IsEmpty(propertyExp));
        }

        private static Expression Contains(Type type, string value, Expression propertyExp)
        {
            var someValue = Expression.Constant(value.ToLower(), typeof(string));

            var nullCheck = GetNullCheckExpression(propertyExp);

            var method = propertyExp.Type.GetMethod("Contains", new[] { type });

            Expression exOut = Expression.Call(propertyExp, typeof(string).GetMethod("ToLower", Type.EmptyTypes));

            exOut = Expression.AndAlso(nullCheck, Expression.Call(exOut, method, someValue));

            return exOut;
        }

        private static Expression NotContains(Type type, string value, Expression propertyExp)
        {
            return Expression.Not(Contains(type, value, propertyExp));
        }

        private static Expression EndsWith(Type type, string value, Expression propertyExp)
        {
            var someValue = Expression.Constant(value.ToLower(), typeof(string));

            var nullCheck = GetNullCheckExpression(propertyExp);

            var method = propertyExp.Type.GetMethod("EndsWith", new[] { type });

            Expression exOut = Expression.Call(propertyExp, typeof(string).GetMethod("ToLower", Type.EmptyTypes));

            exOut = Expression.AndAlso(nullCheck, Expression.Call(exOut, method, someValue));

            return exOut;
        }

        private static Expression NotEndsWith(Type type, string value, Expression propertyExp)
        {
            return Expression.Not(EndsWith(type, value, propertyExp));
        }

        private static Expression BeginsWith(Type type, string value, Expression propertyExp)
        {
            var someValue = Expression.Constant(value.ToLower(), typeof(string));

            var nullCheck = GetNullCheckExpression(propertyExp);

            var method = propertyExp.Type.GetMethod("StartsWith", new[] { type });

            Expression exOut = Expression.Call(propertyExp, typeof(string).GetMethod("ToLower", Type.EmptyTypes));

            exOut = Expression.AndAlso(nullCheck, Expression.Call(exOut, method, someValue));

            return exOut;
        }

        private static Expression NotBeginsWith(Type type, string value, Expression propertyExp)
        {
            return Expression.Not(BeginsWith(type, value, propertyExp));
        }



        private static Expression NotEquals(Type type, string value, Expression propertyExp)
        {
            return Expression.Not(Equals(type, value, propertyExp));
        }



        private static Expression Equals(Type type, string value, Expression propertyExp)
        {
            Expression someValue = GetConstants(type, value, false).First();

            Expression exOut;
            if (type == typeof(string))
            {
                var nullCheck = GetNullCheckExpression(propertyExp);

                exOut = Expression.Call(propertyExp, typeof(string).GetMethod("ToLower", Type.EmptyTypes));
                someValue = Expression.Call(someValue, typeof(string).GetMethod("ToLower", Type.EmptyTypes));
                exOut = Expression.AndAlso(nullCheck, Expression.Equal(exOut, someValue));
            }
            else
            {
                exOut = Expression.Equal(propertyExp, Expression.Convert(someValue, propertyExp.Type));
            }

            return exOut;


        }

        private static Expression LessThan(Type type, string value, Expression propertyExp)
        {
            var someValue = GetConstants(type, value, false).First();

            Expression exOut = Expression.LessThan(propertyExp, Expression.Convert(someValue, propertyExp.Type));


            return exOut;


        }

        private static Expression LessThanOrEqual(Type type, string value, Expression propertyExp)
        {
            var someValue = GetConstants(type, value, false).First();

            Expression exOut = Expression.LessThanOrEqual(propertyExp, Expression.Convert(someValue, propertyExp.Type));


            return exOut;


        }

        private static Expression GreaterThan(Type type, string value, Expression propertyExp)
        {

            var someValue = GetConstants(type, value, false).First();



            Expression exOut = Expression.GreaterThan(propertyExp, Expression.Convert(someValue, propertyExp.Type));


            return exOut;


        }

        private static Expression GreaterThanOrEqual(Type type, string value, Expression propertyExp)
        {
            var someValue = GetConstants(type, value, false).First();

            Expression exOut = Expression.GreaterThanOrEqual(propertyExp, Expression.Convert(someValue, propertyExp.Type));


            return exOut;


        }

        private static Expression Between(Type type, string value, Expression propertyExp)
        {
            var someValue = GetConstants(type, value, true);


            Expression exBelow = Expression.GreaterThanOrEqual(propertyExp, Expression.Convert(someValue[0], propertyExp.Type));
            Expression exAbove = Expression.LessThanOrEqual(propertyExp, Expression.Convert(someValue[1], propertyExp.Type));

            return Expression.And(exBelow, exAbove);


        }

        private static Expression NotBetween(Type type, string value, Expression propertyExp)
        {
            return Expression.Not(Between(type, value, propertyExp));
        }

        private static Expression In(Type type, string value, Expression propertyExp)
        {
            var someValues = GetConstants(type, value, true);

            var nullCheck = GetNullCheckExpression(propertyExp);

            if (IsGenericList(propertyExp.Type))
            {
                var genericType = propertyExp.Type.GetGenericArguments().First();
                var method = propertyExp.Type.GetMethod("Contains", new[] { genericType });
                Expression exOut;

                if (someValues.Count > 1)
                {
                    var valueExpression = Expression.Call(someValues[0], typeof(string).GetMethod("ToLower", Type.EmptyTypes));
                    exOut = Expression.Call(propertyExp, method, Expression.Convert(valueExpression, genericType));
                    var counter = 1;
                    while (counter < someValues.Count)
                    {
                        valueExpression = Expression.Call(someValues[counter], typeof(string).GetMethod("ToLower", Type.EmptyTypes));
                        exOut = Expression.Or(exOut,
                            Expression.Call(propertyExp, method, Expression.Convert(valueExpression, genericType)));
                        counter++;
                    }
                }
                else
                {
                    exOut = Expression.Call(propertyExp, method, Expression.Convert(someValues.First(), genericType));
                }


                return Expression.AndAlso(nullCheck, exOut);
            }
            else
            {
                Expression exOut;

                if (someValues.Count > 1)
                {
                    if (type == typeof(string))
                    {
                        var valueExpression = Expression.Call(someValues[0], typeof(string).GetMethod("ToLower", Type.EmptyTypes));
                        exOut = Expression.Call(propertyExp, typeof(string).GetMethod("ToLower", Type.EmptyTypes));
                        exOut = Expression.Equal(exOut, Expression.Convert(valueExpression, propertyExp.Type));
                        var counter = 1;
                        while (counter < someValues.Count)
                        {
                            valueExpression = Expression.Call(someValues[counter], typeof(string).GetMethod("ToLower", Type.EmptyTypes));
                            exOut = Expression.Or(exOut,
                                Expression.Equal(
                                    Expression.Call(propertyExp, typeof(string).GetMethod("ToLower", Type.EmptyTypes)),
                                    Expression.Convert(valueExpression, propertyExp.Type)));
                            counter++;
                        }
                    }
                    else
                    {
                        exOut = Expression.Equal(propertyExp, Expression.Convert(someValues[0], propertyExp.Type));
                        var counter = 1;
                        while (counter < someValues.Count)
                        {
                            exOut = Expression.Or(exOut,
                                Expression.Equal(propertyExp, Expression.Convert(someValues[counter], propertyExp.Type)));
                            counter++;
                        }
                    }
                }
                else
                {
                    if (type == typeof(string))
                    {
                        var valueExpression = Expression.Call(someValues.First(), typeof(string).GetMethod("ToLower", Type.EmptyTypes));
                        exOut = Expression.Call(propertyExp, typeof(string).GetMethod("ToLower", Type.EmptyTypes));
                        exOut = Expression.Equal(exOut, valueExpression);
                    }
                    else
                    {
                        exOut = Expression.Equal(propertyExp, Expression.Convert(someValues.First(), propertyExp.Type));
                    }
                }


                return Expression.AndAlso(nullCheck, exOut);
            }                    

        }

        private static Expression NotIn(Type type, string value, Expression propertyExp)
        {
            return Expression.Not(In(type, value, propertyExp));
        }

        #endregion

        private static bool IsGenericList(this Type o)
        {
            var isGenericList = false;

            var oType = o;

            if (oType.IsGenericType && ((oType.GetGenericTypeDefinition() == typeof(IEnumerable<>)) || (oType.GetGenericTypeDefinition() == typeof(ICollection<>)) || (oType.GetGenericTypeDefinition() == typeof(List<>))))
                isGenericList = true;

            return isGenericList;
        }

        private static object GetDefaultValue(this Type type)
        {
            return type.GetTypeInfo().IsValueType ? Activator.CreateInstance(type) : null;
        }

    }
}
