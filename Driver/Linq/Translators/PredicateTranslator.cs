﻿/* Copyright 2010-2012 10gen Inc.
*
* Licensed under the Apache License, Version 2.0 (the "License");
* you may not use this file except in compliance with the License.
* You may obtain a copy of the License at
*
* http://www.apache.org/licenses/LICENSE-2.0
*
* Unless required by applicable law or agreed to in writing, software
* distributed under the License is distributed on an "AS IS" BASIS,
* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
* See the License for the specific language governing permissions and
* limitations under the License.
*/

using System;
using System.Collections;
using System.Linq;
using System.Linq.Expressions;
using System.Text.RegularExpressions;

using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Driver.Builders;
using MongoDB.Driver.Linq.Utils;

namespace MongoDB.Driver.Linq
{
    /// <summary>
    /// Translates an expression tree into an IMongo_queryBuilder.
    /// </summary>
    internal class PredicateTranslator
    {
        // private fields
        private readonly BsonSerializationInfoHelper _serializationInfoHelper;
        private readonly UntypedQueryBuilder _queryBuilder;

        // constructors
        /// <summary>
        /// Initializes a new instance of the <see cref="PredicateTranslator"/> class.
        /// </summary>
        /// <param name="serializationHelper">The serialization helper.</param>
        public PredicateTranslator(BsonSerializationInfoHelper serializationHelper)
        {
            _serializationInfoHelper = serializationHelper;
            _queryBuilder = new UntypedQueryBuilder();
        }

        // public methods
        /// <summary>
        /// Builds an IMongoQuery from an expression.
        /// </summary>
        /// <param name="expression">The expression.</param>
        /// <returns></returns>
        public IMongoQuery BuildQuery(Expression expression)
        {
            IMongoQuery query = null;

            switch (expression.NodeType)
            {
                case ExpressionType.AndAlso:
                    query = BuildAndAlsoQuery((BinaryExpression)expression);
                    break;
                case ExpressionType.ArrayIndex:
                    query = BuildBooleanQuery(expression);
                    break;
                case ExpressionType.Call:
                    query = BuildMethodCallQuery((MethodCallExpression)expression);
                    break;
                case ExpressionType.Constant:
                    query = BuildConstantQuery((ConstantExpression)expression);
                    break;
                case ExpressionType.Equal:
                case ExpressionType.GreaterThan:
                case ExpressionType.GreaterThanOrEqual:
                case ExpressionType.LessThan:
                case ExpressionType.LessThanOrEqual:
                case ExpressionType.NotEqual:
                    query = BuildComparisonQuery((BinaryExpression)expression);
                    break;
                case ExpressionType.MemberAccess:
                    query = BuildBooleanQuery(expression);
                    break;
                case ExpressionType.Not:
                    query = BuildNotQuery((UnaryExpression)expression);
                    break;
                case ExpressionType.OrElse:
                    query = BuildOrElseQuery((BinaryExpression)expression);
                    break;
                case ExpressionType.TypeIs:
                    query = BuildTypeIsQuery((TypeBinaryExpression)expression);
                    break;
            }

            if (query == null)
            {
                var message = string.Format("Unsupported where clause: {0}.", ExpressionFormatter.ToString(expression));
                throw new ArgumentException(message);
            }

            return query;
        }

        // private methods
        private IMongoQuery BuildAndAlsoQuery(BinaryExpression binaryExpression)
        {
            return _queryBuilder.And(BuildQuery(binaryExpression.Left), BuildQuery(binaryExpression.Right));
        }

        private IMongoQuery BuildAnyQuery(MethodCallExpression methodCallExpression)
        {
            if (methodCallExpression.Method.DeclaringType == typeof(Enumerable))
            {
                var arguments = methodCallExpression.Arguments.ToArray();
                var serializationInfo = _serializationInfoHelper.GetSerializationInfo(arguments[0]);
                if (arguments.Length == 1)
                {
                    return Query.And(
                        Query.NE(serializationInfo.ElementName, BsonNull.Value),
                        Query.Not(serializationInfo.ElementName).Size(0));
                }
                else if (arguments.Length == 2)
                {
                    var itemSerializationInfo = _serializationInfoHelper.GetItemSerializationInfo("Any", serializationInfo);
                    if (!(itemSerializationInfo.Serializer is IBsonMemberSerializationInfoProvider))
                    {
                        var message = string.Format("Any is only support for items that serialize into documents. The current serializer is {0} and must implement {1} for participation in Any queries.",
                            BsonUtils.GetFriendlyTypeName(itemSerializationInfo.GetType()),
                            BsonUtils.GetFriendlyTypeName(typeof(IBsonMemberSerializationInfoProvider)));
                        throw new NotSupportedException(message);
                    }
                    var itemSerializer = itemSerializationInfo.Serializer;
                    var lambda = (LambdaExpression)arguments[1];
                    _serializationInfoHelper.RegisterExpressionSerializer(lambda.Parameters[0], itemSerializer);
                    var query = BuildQuery(lambda.Body);
                    return _queryBuilder.ElemMatch(serializationInfo.ElementName, query);
                }
            }
            return null;
        }

        private IMongoQuery BuildArrayLengthQuery(Expression variableExpression, ExpressionType operatorType, ConstantExpression constantExpression)
        {
            if (operatorType != ExpressionType.Equal && operatorType != ExpressionType.NotEqual)
            {
                return null;
            }

            if (constantExpression.Type != typeof(int))
            {
                return null;
            }
            var value = ToInt32(constantExpression);

            BsonSerializationInfo serializationInfo = null;

            var unaryExpression = variableExpression as UnaryExpression;
            if (unaryExpression != null && unaryExpression.NodeType == ExpressionType.ArrayLength)
            {
                var arrayMemberExpression = unaryExpression.Operand as MemberExpression;
                if (arrayMemberExpression != null)
                {
                    serializationInfo = _serializationInfoHelper.GetSerializationInfo(arrayMemberExpression);
                }
            }

            var memberExpression = variableExpression as MemberExpression;
            if (memberExpression != null && memberExpression.Member.Name == "Count")
            {
                var arrayMemberExpression = memberExpression.Expression as MemberExpression;
                if (arrayMemberExpression != null)
                {
                    serializationInfo = _serializationInfoHelper.GetSerializationInfo(arrayMemberExpression);
                }
            }

            var methodCallExpression = variableExpression as MethodCallExpression;
            if (methodCallExpression != null && methodCallExpression.Method.Name == "Count" && methodCallExpression.Method.DeclaringType == typeof(Enumerable))
            {
                var arguments = methodCallExpression.Arguments.ToArray();
                if (arguments.Length == 1)
                {
                    var arrayMemberExpression = methodCallExpression.Arguments[0] as MemberExpression;
                    if (arrayMemberExpression != null && arrayMemberExpression.Type != typeof(string))
                    {
                        serializationInfo = _serializationInfoHelper.GetSerializationInfo(arrayMemberExpression);
                    }
                }
            }

            if (serializationInfo != null)
            {
                if (operatorType == ExpressionType.Equal)
                {
                    return _queryBuilder.Size(serializationInfo.ElementName, value);
                }
                else
                {
                    return _queryBuilder.Not(_queryBuilder.Size(serializationInfo.ElementName, value));
                }
            }

            return null;
        }

        private IMongoQuery BuildBooleanQuery(Expression expression)
        {
            if (expression.Type == typeof(bool))
            {
                var serializationInfo = _serializationInfoHelper.GetSerializationInfo(expression);
                return new QueryDocument(serializationInfo.ElementName, true);
            }
            return null;
        }

        private IMongoQuery BuildComparisonQuery(BinaryExpression binaryExpression)
        {
            // the constant could be on either side
            var variableExpression = binaryExpression.Left;
            var constantExpression = binaryExpression.Right as ConstantExpression;
            var operatorType = binaryExpression.NodeType;

            if (constantExpression == null)
            {
                return null;
            }

            var query = BuildArrayLengthQuery(variableExpression, operatorType, constantExpression);
            if (query != null)
            {
                return query;
            }

            query = BuildModQuery(variableExpression, operatorType, constantExpression);
            if (query != null)
            {
                return query;
            }

            query = BuildStringIndexOfQuery(variableExpression, operatorType, constantExpression);
            if (query != null)
            {
                return query;
            }

            query = BuildStringIndexQuery(variableExpression, operatorType, constantExpression);
            if (query != null)
            {
                return query;
            }

            query = BuildStringLengthQuery(variableExpression, operatorType, constantExpression);
            if (query != null)
            {
                return query;
            }

            query = BuildStringCaseComparisonQuery(variableExpression, operatorType, constantExpression);
            if (query != null)
            {
                return query;
            }

            query = BuildTypeComparisonQuery(variableExpression, operatorType, constantExpression);
            if (query != null)
            {
                return query;
            }

            return BuildComparisonQuery(variableExpression, operatorType, constantExpression);
        }

        private IMongoQuery BuildComparisonQuery(Expression variableExpression, ExpressionType operatorType, ConstantExpression constantExpression)
        {
            BsonSerializationInfo serializationInfo = null;
            var value = constantExpression.Value;

            var unaryExpression = variableExpression as UnaryExpression;
            if (unaryExpression != null && (unaryExpression.NodeType == ExpressionType.Convert || unaryExpression.NodeType == ExpressionType.ConvertChecked))
            {
                if (unaryExpression.Operand.Type.IsEnum)
                {
                    var enumType = unaryExpression.Operand.Type;
                    if (unaryExpression.Type == Enum.GetUnderlyingType(enumType))
                    {
                        serializationInfo = _serializationInfoHelper.GetSerializationInfo(unaryExpression.Operand);
                        value = Enum.ToObject(enumType, value); // serialize enum instead of underlying integer
                    }
                }
                else if (
                    unaryExpression.Type.IsGenericType &&
                    unaryExpression.Type.GetGenericTypeDefinition() == typeof(Nullable<>) &&
                    unaryExpression.Operand.Type.IsGenericType &&
                    unaryExpression.Operand.Type.GetGenericTypeDefinition() == typeof(Nullable<>) &&
                    unaryExpression.Operand.Type.GetGenericArguments()[0].IsEnum)
                {
                    var enumType = unaryExpression.Operand.Type.GetGenericArguments()[0];
                    if (unaryExpression.Type.GetGenericArguments()[0] == Enum.GetUnderlyingType(enumType))
                    {
                        serializationInfo = _serializationInfoHelper.GetSerializationInfo(unaryExpression.Operand);
                        if (value != null)
                        {
                            value = Enum.ToObject(enumType, value); // serialize enum instead of underlying integer
                        }
                    }
                }
            }
            else
            {
                serializationInfo = _serializationInfoHelper.GetSerializationInfo(variableExpression);
            }

            if (serializationInfo != null)
            {
                var serializedValue = _serializationInfoHelper.SerializeValue(serializationInfo, value);
                switch (operatorType)
                {
                    case ExpressionType.Equal: return _queryBuilder.EQ(serializationInfo.ElementName, serializedValue);
                    case ExpressionType.GreaterThan: return _queryBuilder.GT(serializationInfo.ElementName, serializedValue);
                    case ExpressionType.GreaterThanOrEqual: return _queryBuilder.GTE(serializationInfo.ElementName, serializedValue);
                    case ExpressionType.LessThan: return _queryBuilder.LT(serializationInfo.ElementName, serializedValue);
                    case ExpressionType.LessThanOrEqual: return _queryBuilder.LTE(serializationInfo.ElementName, serializedValue);
                    case ExpressionType.NotEqual: return _queryBuilder.NE(serializationInfo.ElementName, serializedValue);
                }
            }

            return null;
        }

        private IMongoQuery BuildConstantQuery(ConstantExpression constantExpression)
        {
            var value = constantExpression.Value;
            if (value != null && value.GetType() == typeof(bool))
            {
                // simulate true or false with a tautology or a reverse tautology
                // the particular reverse tautology chosen has the nice property that it uses the index to return no results quickly
                return new QueryDocument("_id", new BsonDocument("$exists", (bool)value));
            }

            return null;
        }

        private IMongoQuery BuildContainsAllQuery(MethodCallExpression methodCallExpression)
        {
            if (methodCallExpression.Method.DeclaringType == typeof(LinqToMongo))
            {
                var arguments = methodCallExpression.Arguments.ToArray();
                if (arguments.Length == 2)
                {
                    var serializationInfo = _serializationInfoHelper.GetSerializationInfo(arguments[0]);
                    var valuesExpression = arguments[1] as ConstantExpression;
                    if (valuesExpression != null)
                    {
                        var itemSerializationInfo = _serializationInfoHelper.GetItemSerializationInfo("ContainsAll", serializationInfo);
                        var serializedValues = _serializationInfoHelper.SerializeValues(itemSerializationInfo, (IEnumerable)valuesExpression.Value);
                        return _queryBuilder.All(serializationInfo.ElementName, serializedValues);
                    }
                }
            }
            return null;
        }

        private IMongoQuery BuildContainsAnyQuery(MethodCallExpression methodCallExpression)
        {
            if (methodCallExpression.Method.DeclaringType == typeof(LinqToMongo))
            {
                var arguments = methodCallExpression.Arguments.ToArray();
                if (arguments.Length == 2)
                {
                    var serializationInfo = _serializationInfoHelper.GetSerializationInfo(arguments[0]);
                    var valuesExpression = arguments[1] as ConstantExpression;
                    if (valuesExpression != null)
                    {
                        var itemSerializationInfo = _serializationInfoHelper.GetItemSerializationInfo("ContainsAny", serializationInfo);
                        var serializedValues = _serializationInfoHelper.SerializeValues(itemSerializationInfo, (IEnumerable)valuesExpression.Value);
                        return _queryBuilder.In(serializationInfo.ElementName, serializedValues);
                    }
                }
            }
            return null;
        }

        private IMongoQuery BuildContainsQuery(MethodCallExpression methodCallExpression)
        {
            if (methodCallExpression.Method.DeclaringType == typeof(string))
            {
                return BuildStringQuery(methodCallExpression);
            }

            if (methodCallExpression.Object != null && methodCallExpression.Object.NodeType == ExpressionType.Constant)
            {
                return BuildInQuery(methodCallExpression);
            }

            BsonSerializationInfo serializationInfo = null;
            ConstantExpression valueExpression = null;
            var arguments = methodCallExpression.Arguments.ToArray();
            if (arguments.Length == 1)
            {
                if (typeof(IEnumerable).IsAssignableFrom(methodCallExpression.Method.DeclaringType))
                {
                    serializationInfo = _serializationInfoHelper.GetSerializationInfo(methodCallExpression.Object);
                    valueExpression = arguments[0] as ConstantExpression;
                }
            }
            else if (arguments.Length == 2)
            {
                if (methodCallExpression.Method.DeclaringType == typeof(Enumerable))
                {
                    if (arguments[0].NodeType == ExpressionType.Constant)
                    {
                        return BuildInQuery(methodCallExpression);
                    }
                    serializationInfo = _serializationInfoHelper.GetSerializationInfo(arguments[0]);
                    valueExpression = arguments[1] as ConstantExpression;
                }
            }

            if (serializationInfo != null && valueExpression != null)
            {
                var itemSerializationInfo = _serializationInfoHelper.GetItemSerializationInfo("Contains", serializationInfo);
                var serializedValue = _serializationInfoHelper.SerializeValue(itemSerializationInfo, valueExpression.Value);
                return _queryBuilder.EQ(serializationInfo.ElementName, serializedValue);
            }

            return null;
        }

        private IMongoQuery BuildEqualsQuery(MethodCallExpression methodCallExpression)
        {
            var arguments = methodCallExpression.Arguments.ToArray();

            // assume that static and instance Equals mean the same thing for all classes (i.e. an equality test)
            Expression firstExpression = null;
            Expression secondExpression = null;
            if (methodCallExpression.Object == null)
            {
                // static Equals method
                if (arguments.Length == 2)
                {
                    firstExpression = arguments[0];
                    secondExpression = arguments[1];
                }
            }
            else
            {
                // instance Equals method
                if (arguments.Length == 1)
                {
                    firstExpression = methodCallExpression.Object;
                    secondExpression = arguments[0];
                }
            }

            if (firstExpression != null && secondExpression != null)
            {
                // the constant could be either expression
                var variableExpression = firstExpression;
                var constantExpression = secondExpression as ConstantExpression;
                if (constantExpression == null)
                {
                    constantExpression = firstExpression as ConstantExpression;
                    variableExpression = secondExpression;
                }

                if (constantExpression == null)
                {
                    return null;
                }

                if (variableExpression.Type == typeof(Type) && constantExpression.Type == typeof(Type))
                {
                    return BuildTypeComparisonQuery(variableExpression, ExpressionType.Equal, constantExpression);
                }

                return BuildComparisonQuery(variableExpression, ExpressionType.Equal, constantExpression);
            }

            return null;
        }

        private IMongoQuery BuildInQuery(MethodCallExpression methodCallExpression)
        {
            var methodDeclaringType = methodCallExpression.Method.DeclaringType;
            var arguments = methodCallExpression.Arguments.ToArray();
            BsonSerializationInfo serializationInfo = null;
            ConstantExpression valuesExpression = null;
            if (methodDeclaringType == typeof(LinqToMongo))
            {
                if (arguments.Length == 2)
                {
                    serializationInfo = _serializationInfoHelper.GetSerializationInfo(arguments[0]);
                    valuesExpression = arguments[1] as ConstantExpression;
                }
            }
            else if (methodDeclaringType == typeof(Enumerable) || methodDeclaringType == typeof(Queryable))
            {
                if (arguments.Length == 2)
                {
                    serializationInfo = _serializationInfoHelper.GetSerializationInfo(arguments[1]);
                    valuesExpression = arguments[0] as ConstantExpression;
                }
            }
            else
            {
                if (methodDeclaringType.IsGenericType)
                {
                    methodDeclaringType = methodDeclaringType.GetGenericTypeDefinition();
                }

                bool contains = methodDeclaringType.GetInterface("ICollection`1") != null;
                if (contains && arguments.Length == 1)
                {
                    serializationInfo = _serializationInfoHelper.GetSerializationInfo(arguments[0]);
                    valuesExpression = methodCallExpression.Object as ConstantExpression;
                }
            }

            if (serializationInfo != null && valuesExpression != null)
            {
                var serializedValues = _serializationInfoHelper.SerializeValues(serializationInfo, (IEnumerable)valuesExpression.Value);
                return _queryBuilder.In(serializationInfo.ElementName, serializedValues);
            }
            return null;
        }

        private IMongoQuery BuildInjectQuery(MethodCallExpression methodCallExpression)
        {
            if (methodCallExpression.Method.DeclaringType == typeof(LinqToMongo))
            {
                var arguments = methodCallExpression.Arguments.ToArray();
                if (arguments.Length == 1)
                {
                    var queryExpression = arguments[0] as ConstantExpression;
                    if (queryExpression != null)
                    {
                        return (IMongoQuery)queryExpression.Value;
                    }
                }
            }
            return null;
        }

        private IMongoQuery BuildIsMatchQuery(MethodCallExpression methodCallExpression)
        {
            if (methodCallExpression.Method.DeclaringType == typeof(Regex))
            {
                var arguments = methodCallExpression.Arguments.ToArray();
                var obj = methodCallExpression.Object;
                if (obj == null)
                {
                    if (arguments.Length == 2 || arguments.Length == 3)
                    {
                        var serializationInfo = _serializationInfoHelper.GetSerializationInfo(arguments[0]);
                        var patternExpression = arguments[1] as ConstantExpression;
                        if (patternExpression != null)
                        {
                            var pattern = patternExpression.Value as string;
                            if (pattern != null)
                            {
                                var options = RegexOptions.None;
                                if (arguments.Length == 3)
                                {
                                    var optionsExpression = arguments[2] as ConstantExpression;
                                    if (optionsExpression == null || optionsExpression.Type != typeof(RegexOptions))
                                    {
                                        return null;
                                    }
                                    options = (RegexOptions)optionsExpression.Value;
                                }
                                var regex = new Regex(pattern, options);
                                return _queryBuilder.Matches(serializationInfo.ElementName, regex);
                            }
                        }
                    }
                }
                else
                {
                    var regexExpression = obj as ConstantExpression;
                    if (regexExpression != null && arguments.Length == 1)
                    {
                        var serializationInfo = _serializationInfoHelper.GetSerializationInfo(arguments[0]);
                        var regex = regexExpression.Value as Regex;
                        if (regex != null)
                        {
                            return _queryBuilder.Matches(serializationInfo.ElementName, regex);
                        }
                    }
                }
            }
            return null;
        }

        private IMongoQuery BuildIsNullOrEmptyQuery(MethodCallExpression methodCallExpression)
        {
            if (methodCallExpression.Method.DeclaringType == typeof(string) && methodCallExpression.Object == null)
            {
                var arguments = methodCallExpression.Arguments.ToArray();
                if (arguments.Length == 1)
                {
                    var serializationInfo = _serializationInfoHelper.GetSerializationInfo(arguments[0]);
                    return _queryBuilder.Or(
                        _queryBuilder.Type(serializationInfo.ElementName, BsonType.Null), // this is the safe way to test for null
                        _queryBuilder.EQ(serializationInfo.ElementName, ""));
                }
            }

            return null;
        }

        private IMongoQuery BuildMethodCallQuery(MethodCallExpression methodCallExpression)
        {
            switch (methodCallExpression.Method.Name)
            {
                case "Any": return BuildAnyQuery(methodCallExpression);
                case "Contains": return BuildContainsQuery(methodCallExpression);
                case "ContainsAll": return BuildContainsAllQuery(methodCallExpression);
                case "ContainsAny": return BuildContainsAnyQuery(methodCallExpression);
                case "EndsWith": return BuildStringQuery(methodCallExpression);
                case "Equals": return BuildEqualsQuery(methodCallExpression);
                case "In": return BuildInQuery(methodCallExpression);
                case "Inject": return BuildInjectQuery(methodCallExpression);
                case "IsMatch": return BuildIsMatchQuery(methodCallExpression);
                case "IsNullOrEmpty": return BuildIsNullOrEmptyQuery(methodCallExpression);
                case "StartsWith": return BuildStringQuery(methodCallExpression);
            }
            return null;
        }

        private IMongoQuery BuildModQuery(Expression variableExpression, ExpressionType operatorType, ConstantExpression constantExpression)
        {
            if (operatorType != ExpressionType.Equal && operatorType != ExpressionType.NotEqual)
            {
                return null;
            }

            if (constantExpression.Type != typeof(int))
            {
                return null;
            }
            var value = ToInt32(constantExpression);

            var modBinaryExpression = variableExpression as BinaryExpression;
            if (modBinaryExpression != null && modBinaryExpression.NodeType == ExpressionType.Modulo)
            {
                var serializationInfo = _serializationInfoHelper.GetSerializationInfo(modBinaryExpression.Left);
                var modulusExpression = modBinaryExpression.Right as ConstantExpression;
                if (modulusExpression != null)
                {
                    var modulus = ToInt32(modulusExpression);
                    if (operatorType == ExpressionType.Equal)
                    {
                        return _queryBuilder.Mod(serializationInfo.ElementName, modulus, value);
                    }
                    else
                    {
                        return _queryBuilder.Not(_queryBuilder.Mod(serializationInfo.ElementName, modulus, value));
                    }
                }
            }

            return null;
        }

        private IMongoQuery BuildNotQuery(UnaryExpression unaryExpression)
        {
            var queryDocument = new QueryDocument(BuildQuery(unaryExpression.Operand).ToBsonDocument());
            return new UntypedQueryBuilder().Not(queryDocument);
        }

        private IMongoQuery BuildOrElseQuery(BinaryExpression binaryExpression)
        {
            return _queryBuilder.Or(BuildQuery(binaryExpression.Left), BuildQuery(binaryExpression.Right));
        }

        private IMongoQuery BuildStringIndexOfQuery(Expression variableExpression, ExpressionType operatorType, ConstantExpression constantExpression)
        {
            // TODO: support other comparison operators
            if (operatorType != ExpressionType.Equal)
            {
                return null;
            }

            if (constantExpression.Type != typeof(int))
            {
                return null;
            }
            var index = ToInt32(constantExpression);

            var methodCallExpression = variableExpression as MethodCallExpression;
            if (methodCallExpression != null &&
                (methodCallExpression.Method.Name == "IndexOf" || methodCallExpression.Method.Name == "IndexOfAny") &&
                methodCallExpression.Method.DeclaringType == typeof(string))
            {
                var serializationInfo = _serializationInfoHelper.GetSerializationInfo(methodCallExpression.Object);

                object value;
                var startIndex = -1;
                var count = -1;

                var args = methodCallExpression.Arguments.ToArray();
                switch (args.Length)
                {
                    case 3:
                        var countExpression = args[2] as ConstantExpression;
                        if (countExpression == null)
                        {
                            return null;
                        }
                        count = ToInt32(countExpression);
                        goto case 2;
                    case 2:
                        var startIndexExpression = args[1] as ConstantExpression;
                        if (startIndexExpression == null)
                        {
                            return null;
                        }
                        startIndex = ToInt32(startIndexExpression);
                        goto case 1;
                    case 1:
                        var valueExpression = args[0] as ConstantExpression;
                        if (valueExpression == null)
                        {
                            return null;
                        }
                        value = valueExpression.Value;
                        break;
                    default:
                        return null;
                }

                string pattern = null;
                if (value.GetType() == typeof(char) || value.GetType() == typeof(char[]))
                {
                    char[] chars;
                    if (value.GetType() == typeof(char))
                    {
                        chars = new char[] { (char)value };
                    }
                    else
                    {
                        chars = (char[])value;
                    }
                    var positiveClass = string.Join("", chars.Select(c => (c == '-') ? "\\-" : (c == ']') ? "\\]" : Regex.Escape(c.ToString())).ToArray());
                    var negativeClass = "[^" + positiveClass + "]";
                    if (chars.Length > 1)
                    {
                        positiveClass = "[" + positiveClass + "]";
                    }

                    if (startIndex == -1)
                    {
                        // the regex for: IndexOf(c) == index 
                        // is: /^[^c]{index}c/
                        pattern = string.Format("^{0}{{{1}}}{2}", negativeClass, index, positiveClass);
                    }
                    else
                    {
                        if (count == -1)
                        {
                            // the regex for: IndexOf(c, startIndex) == index
                            // is: /^.{startIndex}[^c]{index - startIndex}c/
                            pattern = string.Format("^.{{{0}}}{1}{{{2}}}{3}", startIndex, negativeClass, index - startIndex, positiveClass);
                        }
                        else
                        {
                            if (index >= startIndex + count)
                            {
                                // index is outside of the substring so no match is possible
                                return _queryBuilder.NotExists("_id"); // matches no documents
                            }
                            else
                            {
                                // the regex for: IndexOf(c, startIndex, count) == index
                                // is: /^.{startIndex}(?=.{count})[^c]{index - startIndex}c/
                                pattern = string.Format("^.{{{0}}}(?=.{{{1}}}){2}{{{3}}}{4}", startIndex, count, negativeClass, index - startIndex, positiveClass);
                            }
                        }
                    }
                }
                else if (value.GetType() == typeof(string))
                {
                    var escapedString = Regex.Escape((string)value);
                    if (startIndex == -1)
                    {
                        // the regex for: IndexOf(s) == index 
                        // is: /^(?!.{0,index - 1}s).{index}s/
                        pattern = string.Format("^(?!.{{0,{2}}}{0}).{{{1}}}{0}", escapedString, index, index - 1);
                    }
                    else
                    {
                        if (count == -1)
                        {
                            // the regex for: IndexOf(s, startIndex) == index
                            // is: /^.{startIndex}(?!.{0, index - startIndex - 1}s).{index - startIndex}s/
                            pattern = string.Format("^.{{{1}}}(?!.{{0,{2}}}{0}).{{{3}}}{0}", escapedString, startIndex, index - startIndex - 1, index - startIndex);
                        }
                        else
                        {
                            var unescapedLength = ((string)value).Length;
                            if (unescapedLength > startIndex + count - index)
                            {
                                // substring isn't long enough to match
                                return _queryBuilder.NotExists("_id"); // matches no documents
                            }
                            else
                            {
                                // the regex for: IndexOf(s, startIndex, count) == index
                                // is: /^.{startIndex}(?=.{count})(?!.{0,index - startIndex - 1}s).{index - startIndex)s/
                                pattern = string.Format("^.{{{1}}}(?=.{{{2}}})(?!.{{0,{3}}}{0}).{{{4}}}{0}", escapedString, startIndex, count, index - startIndex - 1, index - startIndex);
                            }
                        }
                    }
                }

                if (pattern != null)
                {
                    return _queryBuilder.Matches(serializationInfo.ElementName, pattern, "s");
                }
            }

            return null;
        }

        private IMongoQuery BuildStringIndexQuery(Expression variableExpression, ExpressionType operatorType, ConstantExpression constantExpression)
        {
            var unaryExpression = variableExpression as UnaryExpression;
            if (unaryExpression == null)
            {
                return null;
            }

            if (unaryExpression.NodeType != ExpressionType.Convert || unaryExpression.Type != typeof(int))
            {
                return null;
            }

            var methodCallExpression = unaryExpression.Operand as MethodCallExpression;
            if (methodCallExpression == null)
            {
                return null;
            }

            var method = methodCallExpression.Method;
            if (method.DeclaringType != typeof(string) || method.Name != "get_Chars")
            {
                return null;
            }

            var stringExpression = methodCallExpression.Object;
            if (stringExpression == null)
            {
                return null;
            }

            var args = methodCallExpression.Arguments.ToArray();
            if (args.Length != 1)
            {
                return null;
            }

            var indexExpression = args[0] as ConstantExpression;
            if (indexExpression == null)
            {
                return null;
            }

            if (constantExpression.Type != typeof(int))
            {
                return null;
            }

            var index = ToInt32(indexExpression);
            var value = ToInt32(constantExpression);

            var c = new string((char)value, 1);
            var positiveClass = (c == "-") ? "\\-" : (c == "]") ? "\\]" : Regex.Escape(c);
            var negativeClass = "[^" + positiveClass + "]";

            string characterClass;
            switch (operatorType)
            {
                case ExpressionType.Equal:
                    characterClass = positiveClass;
                    break;
                case ExpressionType.NotEqual:
                    characterClass = negativeClass;
                    break;
                default:
                    return null; // TODO: suport other comparison operators?
            }
            var pattern = string.Format("^.{{{0}}}{1}", index, characterClass);

            var serializationInfo = _serializationInfoHelper.GetSerializationInfo(stringExpression);
            return _queryBuilder.Matches(serializationInfo.ElementName, pattern, "s");
        }

        private IMongoQuery BuildStringLengthQuery(Expression variableExpression, ExpressionType operatorType, ConstantExpression constantExpression)
        {
            if (constantExpression.Type != typeof(int))
            {
                return null;
            }
            var value = ToInt32(constantExpression);

            BsonSerializationInfo serializationInfo = null;

            var memberExpression = variableExpression as MemberExpression;
            if (memberExpression != null && memberExpression.Member.Name == "Length")
            {
                var stringMemberExpression = memberExpression.Expression as MemberExpression;
                if (stringMemberExpression != null && stringMemberExpression.Type == typeof(string))
                {
                    serializationInfo = _serializationInfoHelper.GetSerializationInfo(stringMemberExpression);
                }
            }

            var methodCallExpression = variableExpression as MethodCallExpression;
            if (methodCallExpression != null && methodCallExpression.Method.Name == "Count" && methodCallExpression.Method.DeclaringType == typeof(Enumerable))
            {
                var args = methodCallExpression.Arguments.ToArray();
                if (args.Length == 1)
                {
                    var stringMemberExpression = args[0] as MemberExpression;
                    if (stringMemberExpression != null && stringMemberExpression.Type == typeof(string))
                    {
                        serializationInfo = _serializationInfoHelper.GetSerializationInfo(stringMemberExpression);
                    }
                }
            }

            if (serializationInfo != null)
            {
                string regex = null;
                switch (operatorType)
                {
                    case ExpressionType.NotEqual:
                    case ExpressionType.Equal: regex = @"/^.{" + value.ToString() + "}$/s"; break;
                    case ExpressionType.GreaterThan: regex = @"/^.{" + (value + 1).ToString() + ",}$/s"; break;
                    case ExpressionType.GreaterThanOrEqual: regex = @"/^.{" + value.ToString() + ",}$/s"; break;
                    case ExpressionType.LessThan: regex = @"/^.{0," + (value - 1).ToString() + "}$/s"; break;
                    case ExpressionType.LessThanOrEqual: regex = @"/^.{0," + value.ToString() + "}$/s"; break;
                }
                if (regex != null)
                {
                    if (operatorType == ExpressionType.NotEqual)
                    {
                        return _queryBuilder.Not(_queryBuilder.Matches(serializationInfo.ElementName, regex));
                    }
                    else
                    {
                        return _queryBuilder.Matches(serializationInfo.ElementName, regex);
                    }
                }
            }

            return null;
        }

        private IMongoQuery BuildStringCaseComparisonQuery(Expression variableExpression, ExpressionType operatorType, ConstantExpression constantExpression)
        {
            var methodExpression = variableExpression as MethodCallExpression;
            if (methodExpression != null)
            {
                var sourceExpression = methodExpression.Object as MemberExpression;

                if (methodExpression.Type == typeof(string) && sourceExpression != null)
                {
                    var serializationInfo =  _serializationInfoHelper.GetSerializationInfo(methodExpression.Object);
                    var serializedValue = _serializationInfoHelper.SerializeValue(serializationInfo, constantExpression.Value);
                    var coalescedStringValue = constantExpression.Value == null ? string.Empty : serializedValue.AsString;

                    string regexPattern = "/^" + Regex.Escape(coalescedStringValue) + "$/i";
                    var regex = new BsonRegularExpression(regexPattern);

                    bool caseMismatch = false;

                    if (methodExpression.Method.Name == "ToLower" && (coalescedStringValue != coalescedStringValue.ToLower()))
                        caseMismatch = true;
                    else if (methodExpression.Method.Name == "ToUpper" && (coalescedStringValue != coalescedStringValue.ToUpper()))
                        caseMismatch = true;
                    else if (constantExpression.Value == null)
                        caseMismatch = true;

                    if (operatorType == ExpressionType.Equal)
                    {
                        // if comparing Foo.ToLower() == "Some Non Lower Case String"
                        // then that is always false for all documents
                        if (caseMismatch)
                            return Query.Exists("_id", false);

                        return Query.And(Query.Exists(serializationInfo.ElementName, true),
                                          Query.Matches(serializationInfo.ElementName, regex));
                    }
                    else if (operatorType == ExpressionType.NotEqual)
                    {
                        // if comparing Foo.ToLower() != "Some Non Lower Case String"
                        // then that is always true as long as Foo is set/exists
                        if (caseMismatch)
                            return Query.Exists(serializationInfo.ElementName, true);

                        return Query.And(Query.Exists(serializationInfo.ElementName, true),
                                          Query.Not(serializationInfo.ElementName).Matches(regex));
                    }
                    else
                        return null;
                }
            }

            return null;
        }

        private IMongoQuery BuildStringQuery(MethodCallExpression methodCallExpression)
        {
            if (methodCallExpression.Method.DeclaringType != typeof(string))
            {
                return null;
            }

            var arguments = methodCallExpression.Arguments.ToArray();
            if (arguments.Length != 1)
            {
                return null;
            }

            var stringExpression = methodCallExpression.Object;
            var constantExpression = arguments[0] as ConstantExpression;
            if (constantExpression == null)
            {
                return null;
            }

            var pattern = Regex.Escape((string)constantExpression.Value);
            switch (methodCallExpression.Method.Name)
            {
                case "Contains": pattern = ".*" + pattern + ".*"; break;
                case "EndsWith": pattern = ".*" + pattern; break;
                case "StartsWith": pattern = pattern + ".*"; break; // query optimizer will use index for rooted regular expressions
                default: return null;
            }

            var caseInsensitive = false;
            MethodCallExpression stringMethodCallExpression;
            while ((stringMethodCallExpression = stringExpression as MethodCallExpression) != null)
            {
                var trimStart = false;
                var trimEnd = false;
                Expression trimCharsExpression = null;
                switch (stringMethodCallExpression.Method.Name)
                {
                    case "ToLower":
                        caseInsensitive = true;
                        break;
                    case "ToUpper":
                        caseInsensitive = true;
                        break;
                    case "Trim":
                        trimStart = true;
                        trimEnd = true;
                        trimCharsExpression = stringMethodCallExpression.Arguments.FirstOrDefault();
                        break;
                    case "TrimEnd":
                        trimEnd = true;
                        trimCharsExpression = stringMethodCallExpression.Arguments.First();
                        break;
                    case "TrimStart":
                        trimStart = true;
                        trimCharsExpression = stringMethodCallExpression.Arguments.First();
                        break;
                    default:
                        return null;
                }

                if (trimStart || trimEnd)
                {
                    var trimCharsPattern = GetTrimCharsPattern(trimCharsExpression);
                    if (trimCharsPattern == null)
                    {
                        return null;
                    }

                    if (trimStart)
                    {
                        pattern = trimCharsPattern + pattern;
                    }
                    if (trimEnd)
                    {
                        pattern = pattern + trimCharsPattern;
                    }
                }

                stringExpression = stringMethodCallExpression.Object;
            }

            pattern = "^" + pattern + "$";
            if (pattern.StartsWith("^.*"))
            {
                pattern = pattern.Substring(3);
            }
            if (pattern.EndsWith(".*$"))
            {
                pattern = pattern.Substring(0, pattern.Length - 3);
            }

            var serializationInfo = _serializationInfoHelper.GetSerializationInfo(stringExpression);
            var options = caseInsensitive ? "is" : "s";
            return _queryBuilder.Matches(serializationInfo.ElementName, pattern, options);
        }

        private IMongoQuery BuildTypeComparisonQuery(Expression variableExpression, ExpressionType operatorType, ConstantExpression constantExpression)
        {
            if (operatorType != ExpressionType.Equal)
            {
                // TODO: support NotEqual?
                return null;
            }

            if (constantExpression.Type != typeof(Type))
            {
                return null;
            }
            var actualType = (Type)constantExpression.Value;

            var methodCallExpression = variableExpression as MethodCallExpression;
            if (methodCallExpression == null)
            {
                return null;
            }
            if (methodCallExpression.Method.Name != "GetType" || methodCallExpression.Object == null)
            {
                return null;
            }
            if (methodCallExpression.Arguments.Count != 0)
            {
                return null;
            }

            // TODO: would the object ever not be a ParameterExpression?
            var parameterExpression = methodCallExpression.Object as ParameterExpression;
            if (parameterExpression == null)
            {
                return null;
            }

            var serializationInfo = _serializationInfoHelper.GetSerializationInfo(parameterExpression);
            var nominalType = serializationInfo.NominalType;

            var discriminatorConvention = BsonSerializer.LookupDiscriminatorConvention(nominalType);
            var discriminator = discriminatorConvention.GetDiscriminator(nominalType, actualType);
            if (discriminator == null)
            {
                return new QueryDocument(); // matches everything
            }

            if (discriminator.IsBsonArray)
            {
                var discriminatorArray = discriminator.AsBsonArray;
                var queries = new IMongoQuery[discriminatorArray.Count + 1];
                queries[0] = _queryBuilder.Size(discriminatorConvention.ElementName, discriminatorArray.Count);
                for (var i = 0; i < discriminatorArray.Count; i++)
                {
                    queries[i + 1] = _queryBuilder.EQ(string.Format("{0}.{1}", discriminatorConvention.ElementName, i), discriminatorArray[i]);
                }
                return _queryBuilder.And(queries);
            }
            else
            {
                return _queryBuilder.And(
                    _queryBuilder.NotExists(discriminatorConvention.ElementName + ".0"), // trick to check that element is not an array
                    _queryBuilder.EQ(discriminatorConvention.ElementName, discriminator));
            }
        }

        private IMongoQuery BuildTypeIsQuery(TypeBinaryExpression typeBinaryExpression)
        {
            var nominalType = typeBinaryExpression.Expression.Type;
            var actualType = typeBinaryExpression.TypeOperand;

            var discriminatorConvention = BsonSerializer.LookupDiscriminatorConvention(nominalType);
            var discriminator = discriminatorConvention.GetDiscriminator(nominalType, actualType);
            if (discriminator == null)
            {
                return new QueryDocument(); // matches everything
            }

            if (discriminator.IsBsonArray)
            {
                discriminator = discriminator.AsBsonArray[discriminator.AsBsonArray.Count - 1];
            }

            return _queryBuilder.EQ(discriminatorConvention.ElementName, discriminator);
        }

        private string GetTrimCharsPattern(Expression trimCharsExpression)
        {
            if (trimCharsExpression == null)
            {
                return "\\s*";
            }

            var constantExpression = trimCharsExpression as ConstantExpression;
            if (constantExpression == null || !constantExpression.Type.IsArray || constantExpression.Type.GetElementType() != typeof(char))
            {
                return null;
            }

            var trimChars = (char[])constantExpression.Value;
            if (trimChars.Length == 0)
            {
                return "\\s*";
            }

            // build a pattern that matches the characters to be trimmed
            var characterClass = string.Join("", trimChars.Select(c => (c == '-') ? "\\-" : (c == ']') ? "\\]" : Regex.Escape(c.ToString())).ToArray());
            if (trimChars.Length > 1)
            {
                characterClass = "[" + characterClass + "]";
            }
            return characterClass + "*";
        }

        private int ToInt32(Expression expression)
        {
            if (expression.Type != typeof(int))
            {
                throw new ArgumentOutOfRangeException("expression", "Expected an Expression of Type Int32.");
            }

            var constantExpression = expression as ConstantExpression;
            if (constantExpression == null)
            {
                throw new ArgumentOutOfRangeException("expression", "Expected a ConstantExpression.");
            }

            return (int)constantExpression.Value;
        }
    }
}
