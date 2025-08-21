using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace Mapfy
{
    // ===================== Public API =====================
    public interface IMapper
    {
        TDest Map<TSrc, TDest>(TSrc source);
        TDest Map<TDest>(object source);
        List<TDest> MapList<TSrc, TDest>(IEnumerable<TSrc> source);
    }

    public enum MappingStrategy
    {
        Reflection = 0,
        CompiledExpressions = 1
    }

    public sealed class MapperConfiguration
    {
        private readonly List<TypeMap> _typeMaps = new List<TypeMap>();

        public MapperConfiguration(Action<MapperConfiguration> configure)
        {
            if (configure != null) configure(this);
        }

        public TypeMapBuilder<TSrc, TDest> For<TSrc, TDest>()
        {
            var map = new TypeMap(typeof(TSrc), typeof(TDest));
            _typeMaps.Add(map);
            return new TypeMapBuilder<TSrc, TDest>(map);
        }

        public IMapper CreateMapper()
        {
            // Build registry first
            var registry = new Dictionary<MapKey, TypeMap>();
            foreach (var tm in _typeMaps)
            {
                registry[new MapKey(tm.SourceType, tm.DestinationType)] = tm;
            }

            foreach (var tm in _typeMaps)
                tm.SetRegistry(registry);

            // Precompute reflection plans
            foreach (var tm in _typeMaps)
                if (tm.Strategy == MappingStrategy.Reflection)
                    tm.BuildReflectionPlan();

            // Build compiled delegates
            var building = new HashSet<TypeMap>();
            foreach (var tm in _typeMaps)
                if (tm.Strategy == MappingStrategy.CompiledExpressions)
                    tm.BuildCompiledDelegate(registry, building);

            return new Mapper(registry);
        }
    }

    public sealed class TypeMapBuilder<TSrc, TDest>
    {
        private readonly TypeMap _map;
        internal TypeMapBuilder(TypeMap map)
        {
            _map = map;
            _map.Strategy = MappingStrategy.CompiledExpressions; // default
            _map.CaseInsensitive = true; // default
        }

        public TypeMapBuilder<TSrc, TDest> Strategy(MappingStrategy strategy)
        {
            _map.Strategy = strategy; return this;
        }
        public TypeMapBuilder<TSrc, TDest> CaseInsensitive(bool value = true)
        {
            _map.CaseInsensitive = value; return this;
        }

        public TypeMapBuilder<TSrc, TDest> ForMember<TMember>(Expression<Func<TDest, TMember>> destMember,
            Action<MemberOptions<TSrc, TDest, TMember>> options)
        {
            if (destMember == null) throw new ArgumentNullException("destMember");
            if (options == null) throw new ArgumentNullException("options");

            var memberInfo = GetMemberInfoFromExpression(destMember);
            if (memberInfo == null)
                throw new ArgumentException("Expression must point to a property or field.", "destMember");

            var opt = new MemberOptions<TSrc, TDest, TMember>(memberInfo);
            options(opt);
            _map.ApplyMemberOption(opt);
            return this;
        }

        private static MemberInfo GetMemberInfoFromExpression<TMember>(Expression<Func<TDest, TMember>> expr)
        {
            var body = expr.Body as MemberExpression;
            if (body != null) return body.Member;
            var ue = expr.Body as UnaryExpression;
            if (ue != null && ue.Operand is MemberExpression)
                return ((MemberExpression)ue.Operand).Member;
            return null;
        }
    }

    public sealed class MemberOptions<TSrc, TDest, TMember>
    {
        internal MemberInfo DestinationMember { get; private set; }
        internal bool IsIgnored { get; private set; }
        internal LambdaExpression Resolver { get; private set; }

        internal MemberOptions(MemberInfo destinationMember)
        {
            DestinationMember = destinationMember;
        }

        public MemberOptions<TSrc, TDest, TMember> Ignore()
        {
            IsIgnored = true; return this;
        }
        public MemberOptions<TSrc, TDest, TMember> MapFrom(Expression<Func<TSrc, TMember>> resolver)
        {
            if (resolver == null) throw new ArgumentNullException("resolver");
            Resolver = resolver; return this;
        }
    }

    // ===================== Internals =====================
    internal struct MapKey : IEquatable<MapKey>
    {
        public readonly Type Src;
        public readonly Type Dest;
        public MapKey(Type src, Type dest) { Src = src; Dest = dest; }
        public bool Equals(MapKey other) { return Src == other.Src && Dest == other.Dest; }
        public override bool Equals(object obj) { return obj is MapKey && Equals((MapKey)obj); }
        public override int GetHashCode() { unchecked { return ((Src != null ? Src.GetHashCode() : 0) * 397) ^ (Dest != null ? Dest.GetHashCode() : 0); } }
    }

    internal sealed class TypeMap
    {
        public Type SourceType { get; private set; }
        public Type DestinationType { get; private set; }
        public bool CaseInsensitive { get; set; }
        public MappingStrategy Strategy { get; set; }

        private readonly List<MemberMap> _customMaps = new List<MemberMap>();
        internal Delegate _compiled; // Func<TSrc, TDest>
        private ReflectionPlan _reflectionPlan;
        private IReadOnlyDictionary<MapKey, TypeMap> _registry;

        public TypeMap(Type src, Type dest)
        {
            SourceType = src; DestinationType = dest;
        }

        public void SetRegistry(IReadOnlyDictionary<MapKey, TypeMap> registry) { _registry = registry; }

        public void ApplyMemberOption<TSrc, TDest, TMember>(MemberOptions<TSrc, TDest, TMember> opt)
        {
            if (opt.IsIgnored)
            {
                _customMaps.Add(MemberMap.IgnoreMember(opt.DestinationMember));
                return;
            }
            if (opt.Resolver != null)
            {
                _customMaps.Add(MemberMap.Custom(opt.DestinationMember, opt.Resolver));
            }
        }

        public void BuildCompiledDelegate(IReadOnlyDictionary<MapKey, TypeMap> registry, HashSet<TypeMap> building)
        {
            if (_compiled != null) return;
            if (building.Contains(this))
                throw new InvalidOperationException("Cyclic mapping detected while compiling " + SourceType + " -> " + DestinationType + ".");
            building.Add(this);

            EnsureDefaultCtor(DestinationType);

            var srcParam = Expression.Parameter(SourceType, "src");
            var destVar = Expression.Variable(DestinationType, "dest");
            var assignDest = Expression.Assign(destVar, Expression.New(DestinationType));
            var body = new List<Expression>();
            body.Add(assignDest);

            var destMembers = GetSettableMembers(DestinationType);
            var customMapLookup = new Dictionary<string, MemberMap>(CaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var m in _customMaps) customMapLookup[m.Destination.Name] = m;

            var srcMembers = new Dictionary<string, MemberInfo>(CaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var m in GetReadableMembers(SourceType)) srcMembers[m.Name] = m;

            foreach (var dest in destMembers)
            {
                MemberMap memberMap;
                if (customMapLookup.TryGetValue(dest.Name, out memberMap))
                {
                    if (memberMap.IsIgnored) continue;
                    var valueExpr = BuildResolverExpression(memberMap.Resolver, srcParam, GetMemberType(dest));
                    if (BuildMemberAssign(dest, destVar, valueExpr) != null) body.Add(BuildMemberAssign(dest, destVar, valueExpr));
                    continue;
                }

                MemberInfo srcMember;
                if (!srcMembers.TryGetValue(dest.Name, out srcMember)) continue;

                var srcAccess = Expression.MakeMemberAccess(srcParam, srcMember);
                var destMemberType = GetMemberType(dest);

                Expression value = TryBuildDirectOrConverted(srcAccess, destMemberType);

                if (value == null)
                {
                    // Collections
                    Type srcElem, destElem;
                    if (TryGetEnumerableElementType(srcAccess.Type, out srcElem) && TryGetEnumerableElementType(destMemberType, out destElem))
                    {
                        var projected = BuildSelectProjection(srcAccess, srcElem, destElem, registry, building);
                        Expression materialized;
                        if (projected != null && TryMaterializeCollection(projected, destMemberType, destElem, out materialized))
                        {
                            value = materialized;
                        }
                    }
                    else
                    {
                        // Nested object
                        var nested = TryBuildNestedMapping(srcAccess, destMemberType, registry, building);
                        if (nested != null) value = nested;
                    }
                }

                if (value == null) continue;
                var assign = BuildMemberAssign(dest, destVar, value);
                if (assign != null) body.Add(assign);
            }

            body.Add(destVar);
            var block = Expression.Block(new[] { destVar }, body);

            var lambdaType = typeof(Func<,>).MakeGenericType(SourceType, DestinationType);
            var lambda = Expression.Lambda(lambdaType, block, new[] { srcParam });
            _compiled = lambda.Compile();

            building.Remove(this);
        }

        public void BuildReflectionPlan()
        {
            EnsureDefaultCtor(DestinationType);

            var destMembers = GetSettableMembers(DestinationType);
            var customMapLookup = new Dictionary<string, MemberMap>(CaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var m in _customMaps) customMapLookup[m.Destination.Name] = m;

            var srcMembers = new Dictionary<string, MemberInfo>(CaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var m in GetReadableMembers(SourceType)) srcMembers[m.Name] = m;

            var steps = new List<ReflectionStep>();

            foreach (var dest in destMembers)
            {
                MemberMap memberMap;
                if (customMapLookup.TryGetValue(dest.Name, out memberMap))
                {
                    if (memberMap.IsIgnored) continue;
                    steps.Add(ReflectionStep.Custom(dest, memberMap.Resolver));
                    continue;
                }

                MemberInfo srcMember;
                if (srcMembers.TryGetValue(dest.Name, out srcMember))
                {
                    steps.Add(ReflectionStep.Auto(dest, srcMember));
                }
            }

            _reflectionPlan = new ReflectionPlan(steps.ToArray());
        }

        public TDest MapCompiled<TSrc, TDest>(TSrc src)
        {
            var del = (Func<TSrc, TDest>)_compiled;
            return del(src);
        }

        public TDest MapReflection<TSrc, TDest>(TSrc src)
        {
            var dest = Activator.CreateInstance<TDest>();
            foreach (var step in _reflectionPlan.Steps)
            {
                if (step.Kind == ReflectionStepKind.Custom)
                {
                    var value = InvokeResolver(step.Resolver, src);
                    AssignViaReflection(step.Destination, dest, value);
                    continue;
                }

                var valueObj = ReadMember(src, step.Source);
                var destType = GetMemberType(step.Destination);

                if (valueObj == null)
                {
                    AssignViaReflection(step.Destination, dest, null);
                    continue;
                }

                // Collections
                Type srcElem, destElem;
                if (TryGetEnumerableElementType(valueObj.GetType(), out srcElem) && TryGetEnumerableElementType(destType, out destElem))
                {
                    var mappedCollection = MapCollectionRuntime(valueObj, srcElem, destType, destElem);
                    AssignViaReflection(step.Destination, dest, mappedCollection);
                    continue;
                }

                // Nested via registry
                if (_registry != null)
                {
                    TypeMap nestedMap;
                    if (_registry.TryGetValue(new MapKey(valueObj.GetType(), destType), out nestedMap))
                    {
                        var nestedValue = nestedMap.MapObject(valueObj);
                        AssignViaReflection(step.Destination, dest, nestedValue);
                        continue;
                    }
                }

                // Direct conversion
                if (!destType.IsInstanceOfType(valueObj))
                {
                    var converted = TryChangeType(valueObj, destType);
                    if (!converted.Item1) continue;
                    valueObj = converted.Item2;
                }

                AssignViaReflection(step.Destination, dest, valueObj);
            }
            return dest;
        }

        public object MapObject(object src)
        {
            if (src == null) return null;
            if (Strategy == MappingStrategy.CompiledExpressions && _compiled != null)
            {
                return _compiled.DynamicInvoke(src);
            }
            var method = typeof(TypeMap).GetMethod("MapReflectionGeneric", BindingFlags.NonPublic | BindingFlags.Instance)
                .MakeGenericMethod(SourceType, DestinationType);
            return method.Invoke(this, new object[] { src });
        }

        private TDest MapReflectionGeneric<TSrc, TDest>(object srcObj)
        {
            return MapReflection<TSrc, TDest>((TSrc)srcObj);
        }

        private TDest MapViaRuntime<TSrc, TDest>(TSrc src)
        {
            return (TDest)MapObject(src);
        }

        private static Tuple<bool, object> TryChangeType(object value, Type targetType)
        {
            try
            {
                if (value == null) return Tuple.Create(true, (object)null);
                if (targetType.IsAssignableFrom(value.GetType())) return Tuple.Create(true, value);
                if (targetType.IsEnum && value is string)
                    return Tuple.Create(true, (object)Enum.Parse(targetType, (string)value));
                if (targetType == typeof(Guid) && value is string)
                    return Tuple.Create(true, (object)Guid.Parse((string)value));
                var converted = Convert.ChangeType(value, targetType);
                return Tuple.Create(true, converted);
            }
            catch
            {
                return Tuple.Create(false, (object)null);
            }
        }

        private static object InvokeResolver(LambdaExpression resolver, object src)
        {
            return resolver.Compile().DynamicInvoke(src);
        }

        private static object ReadMember(object obj, MemberInfo member)
        {
            var p = member as PropertyInfo;
            if (p != null)
            {
                var getter = p.GetGetMethod(true);
                return getter != null ? p.GetValue(obj, null) : null;
            }
            var f = member as FieldInfo;
            if (f != null) return f.GetValue(obj);
            return null;
        }

        private static void AssignViaReflection(MemberInfo dest, object target, object value)
        {
            var p = dest as PropertyInfo;
            if (p != null)
            {
                var setter = p.GetSetMethod(true);
                if (setter != null) p.SetValue(target, value, null);
                return;
            }
            var f = dest as FieldInfo;
            if (f != null) { f.SetValue(target, value); return; }
        }

        private static Expression TryBuildConvert(Expression source, Type targetType)
        {
            try
            {
                if (targetType.IsAssignableFrom(source.Type)) return source;

                // Enum.Parse(Type, string)
                if (targetType.IsEnum && source.Type == typeof(string))
                {
                    var parse = typeof(Enum).GetMethod("Parse", new[] { typeof(Type), typeof(string) });
                    return Expression.Convert(
                        Expression.Call(parse, Expression.Constant(targetType, typeof(Type)), source),
                        targetType);
                }

                if (targetType == typeof(Guid) && source.Type == typeof(string))
                {
                    var parse = typeof(Guid).GetMethod("Parse", new[] { typeof(string) });
                    return Expression.Call(parse, source);
                }

                return Expression.Convert(source, targetType);
            }
            catch
            {
                return null;
            }
        }

        private static Expression BuildResolverExpression(LambdaExpression resolver, ParameterExpression srcParam, Type destMemberType)
        {
            var body = new ReplaceParameterVisitor(resolver.Parameters[0], srcParam).Visit(resolver.Body);
            if (body.Type == destMemberType) return body;
            var converted = TryBuildConvert(body, destMemberType);
            if (converted == null)
                throw new InvalidOperationException("Cannot convert custom MapFrom expression from " + body.Type + " to " + destMemberType + ".");
            return converted;
        }

        private static BinaryExpression BuildMemberAssign(MemberInfo dest, ParameterExpression destVar, Expression value)
        {
            var p = dest as PropertyInfo;
            if (p != null)
            {
                var setter = p.GetSetMethod(true);
                if (setter != null) return Expression.Assign(Expression.Property(destVar, p), value);
                return null;
            }
            var f = dest as FieldInfo;
            if (f != null) return Expression.Assign(Expression.Field(destVar, f), value);
            return null;
        }

        private static void EnsureDefaultCtor(Type t)
        {
            if (t.IsValueType) return;
            if (t.GetConstructor(Type.EmptyTypes) == null)
                throw new InvalidOperationException("Destination type " + t + " must have a public parameterless constructor.");
        }

        private static IEnumerable<MemberInfo> GetSettableMembers(Type t)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
            var list = new List<MemberInfo>();
            foreach (var p in t.GetProperties(flags)) if (p.GetSetMethod(true) != null) list.Add(p);
            foreach (var f in t.GetFields(flags)) list.Add(f);
            return list;
        }

        private static IEnumerable<MemberInfo> GetReadableMembers(Type t)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
            var list = new List<MemberInfo>();
            foreach (var p in t.GetProperties(flags)) if (p.GetGetMethod(true) != null) list.Add(p);
            foreach (var f in t.GetFields(flags)) list.Add(f);
            return list;
        }

        // ============ Compiled helpers ============
        private static Expression TryBuildDirectOrConverted(Expression srcAccess, Type destMemberType)
        {
            if (destMemberType.IsAssignableFrom(srcAccess.Type)) return srcAccess;
            return TryBuildConvert(srcAccess, destMemberType);
        }

        private static bool TryGetEnumerableElementType(Type t, out Type elemType)
        {
            if (t.IsArray)
            {
                elemType = t.GetElementType();
                return true;
            }
            if (typeof(IEnumerable).IsAssignableFrom(t) && t.IsGenericType)
            {
                var args = t.GetGenericArguments();
                foreach (var gi in args)
                {
                    var ienum = typeof(IEnumerable<>).MakeGenericType(gi);
                    if (ienum.IsAssignableFrom(t)) { elemType = gi; return true; }
                }
            }
            elemType = null; return false;
        }

        private static Expression BuildSelectProjection(Expression srcEnumerable, Type srcElem, Type destElem,
            IReadOnlyDictionary<MapKey, TypeMap> registry, HashSet<TypeMap> building)
        {
            var param = Expression.Parameter(srcElem, "it");
            Expression body = null;

            if (destElem.IsAssignableFrom(srcElem))
            {
                body = srcElem == destElem ? (Expression)param : Expression.Convert(param, destElem);
            }
            else
            {
                TypeMap nestedMap;
                if (registry.TryGetValue(new MapKey(srcElem, destElem), out nestedMap))
                {
                    if (nestedMap.Strategy == MappingStrategy.CompiledExpressions)
                    {
                        nestedMap.BuildCompiledDelegate(registry, building);
                        var funcType = typeof(Func<,>).MakeGenericType(srcElem, destElem);
                        var delConst = Expression.Constant(nestedMap._compiled, funcType);
                        body = Expression.Invoke(delConst, param);
                    }
                    else
                    {
                        var mi = typeof(TypeMap).GetMethod("MapViaRuntime", BindingFlags.NonPublic | BindingFlags.Instance)
                            .MakeGenericMethod(srcElem, destElem);
                        body = Expression.Call(Expression.Constant(nestedMap), mi, param);
                    }
                }
                else
                {
                    var converted = TryBuildConvert(param, destElem);
                    if (converted == null) return null;
                    body = converted;
                }
            }

            var selectorType = typeof(Func<,>).MakeGenericType(srcElem, destElem);
            var selector = Expression.Lambda(selectorType, body, new[] { param });
            var selectMi = GetEnumerableMethod("Select", 2).MakeGenericMethod(srcElem, destElem);
            return Expression.Call(selectMi, srcEnumerable, selector);
        }

        private static bool TryMaterializeCollection(Expression projected, Type destCollectionType, Type destElem, out Expression materialized)
        {
            // Array
            if (destCollectionType.IsArray)
            {
                var toArrayMi = GetEnumerableMethod("ToArray", 1).MakeGenericMethod(destElem);
                materialized = Expression.Call(toArrayMi, projected);
                return true;
            }

            var listType = typeof(List<>).MakeGenericType(destElem);
            if (destCollectionType.IsAssignableFrom(listType))
            {
                var toListMi = GetEnumerableMethod("ToList", 1).MakeGenericMethod(destElem);
                materialized = Expression.Call(toListMi, projected);
                return true;
            }

            // HashSet<T>: prefer ctor(IEnumerable<T>)
            if (destCollectionType.IsGenericType && destCollectionType.GetGenericTypeDefinition() == typeof(HashSet<>))
            {
                var ctor = destCollectionType.GetConstructor(new[] { typeof(IEnumerable<>).MakeGenericType(destElem) });
                if (ctor != null)
                {
                    materialized = Expression.New(ctor, projected);
                    return true;
                }
            }

            // Any type with ctor(IEnumerable<T>)
            var anyCtor = destCollectionType.GetConstructor(new[] { typeof(IEnumerable<>).MakeGenericType(destElem) });
            if (anyCtor != null)
            {
                materialized = Expression.New(anyCtor, projected);
                return true;
            }

            materialized = projected; // leave as IEnumerable<T>
            return false;
        }

        private static Expression TryBuildNestedMapping(Expression srcAccess, Type destMemberType,
            IReadOnlyDictionary<MapKey, TypeMap> registry, HashSet<TypeMap> building)
        {
            TypeMap nested;
            if (!registry.TryGetValue(new MapKey(srcAccess.Type, destMemberType), out nested)) return null;

            if (nested.Strategy == MappingStrategy.CompiledExpressions)
            {
                nested.BuildCompiledDelegate(registry, building);
                var funcType = typeof(Func<,>).MakeGenericType(srcAccess.Type, destMemberType);
                var delConst = Expression.Constant(nested._compiled, funcType);
                return Expression.Invoke(delConst, srcAccess);
            }
            else
            {
                var mi = typeof(TypeMap).GetMethod("MapViaRuntime", BindingFlags.NonPublic | BindingFlags.Instance)
                    .MakeGenericMethod(srcAccess.Type, destMemberType);
                return Expression.Call(Expression.Constant(nested), mi, srcAccess);
            }
        }

        private static MethodInfo GetEnumerableMethod(string name, int genericArity)
        {
            var methods = typeof(Enumerable).GetMethods(BindingFlags.Public | BindingFlags.Static);
            foreach (var m in methods)
            {
                if (m.Name == name && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == genericArity)
                    return m;
            }
            throw new MissingMethodException("System.Linq.Enumerable." + name + "<" + new string(',', genericArity - 1) + "> not found.");
        }

        // ============ Reflection helpers (collections) ============
        private object MapCollectionRuntime(object srcEnumerable, Type srcElem, Type destCollectionType, Type destElem)
        {
            var listType = typeof(List<>).MakeGenericType(destElem);
            var list = (IList)Activator.CreateInstance(listType);

            foreach (var item in (IEnumerable)srcEnumerable)
            {
                object mapped = item;
                if (item != null)
                {
                    var itType = item.GetType();
                    if (!destElem.IsAssignableFrom(itType))
                    {
                        if (_registry != null)
                        {
                            TypeMap nested;
                            if (_registry.TryGetValue(new MapKey(itType, destElem), out nested))
                            {
                                mapped = nested.MapObject(item);
                            }
                            else
                            {
                                var conv = TryChangeType(item, destElem);
                                if (!conv.Item1) continue;
                                mapped = conv.Item2;
                            }
                        }
                    }
                }
                list.Add(mapped);
            }

            if (destCollectionType.IsArray)
            {
                var arr = Array.CreateInstance(destElem, list.Count);
                list.CopyTo((Array)arr, 0);
                return arr;
            }

            var listAssignable = typeof(List<>).MakeGenericType(destElem);
            if (destCollectionType.IsAssignableFrom(listAssignable))
            {
                return list;
            }

            if (destCollectionType.IsGenericType && destCollectionType.GetGenericTypeDefinition() == typeof(HashSet<>))
            {
                var ctor = destCollectionType.GetConstructor(new[] { typeof(IEnumerable<>).MakeGenericType(destElem) });
                if (ctor != null) return ctor.Invoke(new object[] { list });
            }

            var anyCtor = destCollectionType.GetConstructor(new[] { typeof(IEnumerable<>).MakeGenericType(destElem) });
            if (anyCtor != null) return anyCtor.Invoke(new object[] { list });

            // fallback
            var last = TryChangeType(list, destCollectionType);
            return last.Item1 ? last.Item2 : null;
        }

        // Helpers to get member type without switch expressions
        private static Type GetMemberType(MemberInfo m)
        {
            var p = m as PropertyInfo; if (p != null) return p.PropertyType;
            var f = m as FieldInfo; if (f != null) return f.FieldType;
            throw new NotSupportedException();
        }
    }

    internal sealed class ReplaceParameterVisitor : ExpressionVisitor
    {
        private readonly ParameterExpression _from;
        private readonly Expression _to;
        public ReplaceParameterVisitor(ParameterExpression from, Expression to) { _from = from; _to = to; }
        protected override Expression VisitParameter(ParameterExpression node)
        {
            return node == _from ? _to : base.VisitParameter(node);
        }
    }

    internal sealed class MemberMap
    {
        public MemberInfo Destination { get; private set; }
        public bool IsIgnored { get; private set; }
        public LambdaExpression Resolver { get; private set; }

        private MemberMap(MemberInfo destination, bool isIgnored, LambdaExpression resolver)
        {
            Destination = destination; IsIgnored = isIgnored; Resolver = resolver;
        }
        public static MemberMap IgnoreMember(MemberInfo destination) { return new MemberMap(destination, true, null); }
        public static MemberMap Custom(MemberInfo destination, LambdaExpression resolver) { return new MemberMap(destination, false, resolver); }
    }

    internal enum ReflectionStepKind { Auto, Custom }

    internal sealed class ReflectionStep
    {
        public ReflectionStepKind Kind { get; private set; }
        public MemberInfo Destination { get; private set; }
        public MemberInfo Source { get; private set; }
        public LambdaExpression Resolver { get; private set; }
        private ReflectionStep(ReflectionStepKind kind, MemberInfo dest, MemberInfo src, LambdaExpression resolver)
        {
            Kind = kind; Destination = dest; Source = src; Resolver = resolver;
        }
        public static ReflectionStep Auto(MemberInfo dest, MemberInfo src) { return new ReflectionStep(ReflectionStepKind.Auto, dest, src, null); }
        public static ReflectionStep Custom(MemberInfo dest, LambdaExpression resolver) { return new ReflectionStep(ReflectionStepKind.Custom, dest, null, resolver); }
    }

    internal sealed class ReflectionPlan
    {
        public ReflectionStep[] Steps { get; private set; }
        public ReflectionPlan(ReflectionStep[] steps) { Steps = steps; }
    }

    internal sealed class Mapper : IMapper
    {
        private readonly Dictionary<MapKey, TypeMap> _maps;
        public Mapper(IReadOnlyDictionary<MapKey, TypeMap> maps)
        {
            _maps = new Dictionary<MapKey, TypeMap>();
            foreach (var kv in maps) _maps[kv.Key] = kv.Value;
        }

        public TDest Map<TSrc, TDest>(TSrc source)
        {
            TypeMap tm;
            if (!_maps.TryGetValue(new MapKey(typeof(TSrc), typeof(TDest)), out tm))
                throw new InvalidOperationException("No map registered for " + typeof(TSrc) + " -> " + typeof(TDest) + ".");

            if (tm.Strategy == MappingStrategy.CompiledExpressions)
                return tm.MapCompiled<TSrc, TDest>(source);

            return tm.MapReflection<TSrc, TDest>(source);
        }

        public TDest Map<TDest>(object source)
        {
            if (source == null) return default(TDest);
            var srcType = source.GetType();
            TypeMap tm;
            if (!_maps.TryGetValue(new MapKey(srcType, typeof(TDest)), out tm))
            {
                // Fallback: try assignable maps (base/interface)
                foreach (var kv in _maps)
                {
                    if (kv.Key.Dest == typeof(TDest) && kv.Key.Src.IsAssignableFrom(srcType))
                    { tm = kv.Value; goto DO_MAP; }
                }
                throw new InvalidOperationException("No map registered for " + srcType + " -> " + typeof(TDest) + ".");
            }
        DO_MAP:
            var obj = tm.MapObject(source);
            return (TDest)obj;
        }

        public List<TDest> MapList<TSrc, TDest>(IEnumerable<TSrc> source)
        {
            return source.Select(Map<TSrc, TDest>).ToList();
        }
    }

    // ============ Global context & extensions ============
    public static class MapfyContext
    {
        private static IMapper _default;
        public static IMapper Default { get { return _default; } set { _default = value; } }
        public static void SetDefault(IMapper mapper) { Default = mapper; }
    }

    public static class MapfyExtensions
    {
        public static TDest Map<TDest>(this object source)
        {
            if (MapfyContext.Default == null)
                throw new InvalidOperationException("MapfyContext.Default is not set. Call MapfyContext.SetDefault(mapper) first.");
            return MapfyContext.Default.Map<TDest>(source);
        }

        public static TDest Map<TDest>(this object source, IMapper mapper)
        {
            if (mapper == null) throw new ArgumentNullException("mapper");
            return mapper.Map<TDest>(source);
        }
    }


}
