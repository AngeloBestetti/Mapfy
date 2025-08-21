using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace Mapfy
{
    public interface IMapper
    {
        TDest Map<TSrc, TDest>(TSrc source);
    }

    /// <summary>
    /// Why: Choose between a simpler reflection-based mapper or a faster expression-compiled mapper.
    /// </summary>
    public enum MappingStrategy
    {
        Reflection = 0,
        CompiledExpressions = 1
    }

    /// <summary>
    /// Why: Root configuration; collects type maps then builds an IMapper with compiled delegates.
    /// </summary>
    public sealed class MapperConfiguration
    {
        private readonly List<TypeMap> _typeMaps = new List<TypeMap>();

        public MapperConfiguration(Action<MapperConfiguration> configure)
        {
            configure?.Invoke(this);
        }

        public TypeMapBuilder<TSrc, TDest> For<TSrc, TDest>()
        {
            var map = new TypeMap(typeof(TSrc), typeof(TDest));
            _typeMaps.Add(map);
            return new TypeMapBuilder<TSrc, TDest>(map);
        }

        public IMapper CreateMapper()
        {
            foreach (var tm in _typeMaps)
            {
                if (tm.Strategy == MappingStrategy.CompiledExpressions)
                {
                    tm.BuildCompiledDelegate();
                }
                else
                {
                    tm.BuildReflectionPlan();
                }
            }
            return new Mapper(_typeMaps);
        }
    }

    /// <summary>
    /// Why: Fluent builder to configure a specific TSrc->TDest map (custom members, strategy, case-sensitivity).
    /// </summary>
    public sealed class TypeMapBuilder<TSrc, TDest>
    {
        private readonly TypeMap _map;

        internal TypeMapBuilder(TypeMap map)
        {
            _map = map;
            _map.Strategy = MappingStrategy.CompiledExpressions; // sensible default
            _map.CaseInsensitive = true; // sensible default
        }

        public TypeMapBuilder<TSrc, TDest> Strategy(MappingStrategy strategy)
        {
            _map.Strategy = strategy;
            return this;
        }

        public TypeMapBuilder<TSrc, TDest> CaseInsensitive(bool value = true)
        {
            _map.CaseInsensitive = value;
            return this;
        }

        public TypeMapBuilder<TSrc, TDest> ForMember<TMember>(Expression<Func<TDest, TMember>> destMember,
            Action<MemberOptions<TSrc, TDest, TMember>> options)
        {
            if (destMember is null) throw new ArgumentNullException(nameof(destMember));
            if (options is null) throw new ArgumentNullException(nameof(options));

            var memberInfo = GetMemberInfoFromExpression(destMember);
            if (memberInfo is null)
                throw new ArgumentException("Expression must point to a property or field.", nameof(destMember));

            var opt = new MemberOptions<TSrc, TDest, TMember>(memberInfo);
            options(opt);
            _map.ApplyMemberOption(opt);
            return this;
        }

        private static MemberInfo GetMemberInfoFromExpression<TMember>(Expression<Func<TDest, TMember>> expr)
        {
            if (expr.Body is MemberExpression me) return me.Member;
            if (expr.Body is UnaryExpression ue && ue.Operand is MemberExpression me2) return me2.Member;
            return null!;
        }
    }

    /// <summary>
    /// Why: Options for a single destination member.
    /// </summary>
    public sealed class MemberOptions<TSrc, TDest, TMember>
    {
        internal MemberInfo DestinationMember { get; }
        internal bool IgnoreSet { get; private set; }
        internal LambdaExpression? Resolver { get; private set; }

        internal MemberOptions(MemberInfo destinationMember)
        {
            DestinationMember = destinationMember;
        }

        public MemberOptions<TSrc, TDest, TMember> Ignore()
        {
            IgnoreSet = true;
            return this;
        }

        public MemberOptions<TSrc, TDest, TMember> MapFrom(Expression<Func<TSrc, TMember>> resolver)
        {
            Resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            return this;
        }
    }

    /// <summary>
    /// Why: Internal representation of a configured map between types.
    /// </summary>
    internal sealed class TypeMap
    {
        public Type SourceType { get; }
        public Type DestinationType { get; }
        public bool CaseInsensitive { get; set; }
        public MappingStrategy Strategy { get; set; }

        private readonly List<MemberMap> _customMaps = new List<MemberMap>();

        // Compiled delegate for fast mapping: boxed to object to store generically.
        private Delegate? _compiled;

        // Reflection plan precomputed for speed
        private ReflectionPlan? _reflectionPlan;

        public TypeMap(Type src, Type dest)
        {
            SourceType = src;
            DestinationType = dest;
        }

        public void ApplyMemberOption<TSrc, TDest, TMember>(MemberOptions<TSrc, TDest, TMember> opt)
        {
            if (opt.IgnoreSet)
            {
                _customMaps.Add(MemberMap.IgnoreMember(opt.DestinationMember));
                return;
            }

            if (opt.Resolver != null)
            {
                _customMaps.Add(MemberMap.Custom(opt.DestinationMember, opt.Resolver));
                return;
            }
        }

        public void BuildCompiledDelegate()
        {
            EnsureDefaultCtor(DestinationType);

            var srcParam = Expression.Parameter(SourceType, "src");
            var destVar = Expression.Variable(DestinationType, "dest");
            var assignDest = Expression.Assign(destVar, Expression.New(DestinationType));

            var body = new List<Expression> { assignDest };

            var destMembers = GetSettableMembers(DestinationType);
            var customMapLookup = _customMaps.ToDictionary(m => m.Destination.Name, m => m, CaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            var srcMembers = GetReadableMembers(SourceType)
                .ToDictionary(m => m.Name, m => m, CaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            foreach (var dest in destMembers)
            {
                if (customMapLookup.TryGetValue(dest.Name, out var memberMap))
                {
                    if (memberMap.IsIgnored) continue;

                    var valueExpr = BuildResolverExpression(memberMap.Resolver!, srcParam, dest.GetMemberType());
                    var assign = BuildMemberAssign(dest, destVar, valueExpr);
                    if (assign != null) body.Add(assign);
                    continue;
                }

                if (srcMembers.TryGetValue(dest.Name, out var srcMember))
                {
                    var srcAccess = Expression.MakeMemberAccess(srcParam, srcMember);
                    Expression value = srcAccess;
                    var destType = dest.GetMemberType();

                    if (!destType.IsAssignableFrom(srcAccess.Type))
                    {
                        var conv = TryBuildConvert(srcAccess, destType);
                        if (conv == null) continue;
                        value = conv;
                    }

                    var assign = BuildMemberAssign(dest, destVar, value);
                    if (assign != null) body.Add(assign);
                }
            }

            body.Add(destVar);
            var block = Expression.Block(new[] { destVar }, body);

            var lambdaType = typeof(Func<,>).MakeGenericType(SourceType, DestinationType);
            var lambda = Expression.Lambda(lambdaType, block, srcParam);
            _compiled = lambda.Compile();
        }

        public void BuildReflectionPlan()
        {
            EnsureDefaultCtor(DestinationType);

            var destMembers = GetSettableMembers(DestinationType);
            var customMapLookup = _customMaps.ToDictionary(m => m.Destination.Name, m => m, CaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var srcMembers = GetReadableMembers(SourceType)
                .ToDictionary(m => m.Name, m => m, CaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            var steps = new List<ReflectionStep>();

            foreach (var dest in destMembers)
            {
                if (customMapLookup.TryGetValue(dest.Name, out var memberMap))
                {
                    if (memberMap.IsIgnored) continue;
                    steps.Add(ReflectionStep.Custom(dest, memberMap.Resolver!));
                    continue;
                }

                if (srcMembers.TryGetValue(dest.Name, out var srcMember))
                {
                    steps.Add(ReflectionStep.Auto(dest, srcMember));
                }
            }

            _reflectionPlan = new ReflectionPlan(steps.ToArray());
        }

        public TDest MapCompiled<TSrc, TDest>(TSrc src)
        {
            var del = (Func<TSrc, TDest>)_compiled!;
            return del(src);
        }

        public TDest MapReflection<TSrc, TDest>(TSrc src)
        {
            var dest = Activator.CreateInstance<TDest>();
            foreach (var step in _reflectionPlan!.Steps)
            {
                if (step.Kind == ReflectionStepKind.Custom)
                {
                    var value = InvokeResolver(step.Resolver!, src);
                    AssignViaReflection(step.Destination, dest!, value);
                }
                else
                {
                    var value = ReadMember(src!, step.Source!);
                    if (value is null)
                    {
                        AssignViaReflection(step.Destination, dest!, null);
                        continue;
                    }

                    var destType = step.Destination.GetMemberType();
                    if (!destType.IsInstanceOfType(value))
                    {
                        var converted = TryChangeType(value, destType);
                        if (!converted.Success) continue;
                        value = converted.Value;
                    }

                    AssignViaReflection(step.Destination, dest!, value);
                }
            }
            return dest;
        }

        private static (bool Success, object? Value) TryChangeType(object? value, Type targetType)
        {
            try
            {
                if (value == null) return (true, null);
                if (targetType.IsAssignableFrom(value.GetType())) return (true, value);
                if (targetType.IsEnum && value is string s) return (true, Enum.Parse(targetType, s));
                if (targetType == typeof(Guid) && value is string sg) return (true, Guid.Parse(sg));
                var converted = Convert.ChangeType(value, targetType);
                return (true, converted);
            }
            catch
            {
                return (false, null);
            }
        }

        private static object? InvokeResolver(LambdaExpression resolver, object src)
        {
            return resolver.Compile().DynamicInvoke(src);
        }

        private static object? ReadMember(object obj, MemberInfo member)
        {
            return member switch
            {
                PropertyInfo p => p.GetGetMethod(true) != null ? p.GetValue(obj) : null,
                FieldInfo f => f.GetValue(obj),
                _ => null
            };
        }

        private static void AssignViaReflection(MemberInfo dest, object target, object? value)
        {
            switch (dest)
            {
                case PropertyInfo p when p.GetSetMethod(true) != null:
                    p.SetValue(target, value);
                    break;
                case FieldInfo f:
                    f.SetValue(target, value);
                    break;
            }
        }

        private static Expression? TryBuildConvert(Expression source, Type targetType)
        {
            try
            {
                if (targetType.IsAssignableFrom(source.Type)) return source;

                if (targetType.IsEnum && source.Type == typeof(string))
                {
                    var parse = typeof(Enum).GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .First(m => m.Name == nameof(Enum.Parse) && m.GetParameters().Length == 2)
                        .MakeGenericMethod(targetType);
                    return Expression.Call(parse, source);
                }

                if (targetType == typeof(Guid) && source.Type == typeof(string))
                {
                    var parse = typeof(Guid).GetMethod(nameof(Guid.Parse), new[] { typeof(string) })!;
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
            var body = new ReplaceParameterVisitor(resolver.Parameters[0], srcParam).Visit(resolver.Body)!;
            if (body.Type == destMemberType) return body;
            var converted = TryBuildConvert(body, destMemberType);
            if (converted == null)
            {
                throw new InvalidOperationException($"Cannot convert custom MapFrom expression from {body.Type} to {destMemberType}.");
            }
            return converted;
        }

        private static BinaryExpression? BuildMemberAssign(MemberInfo dest, ParameterExpression destVar, Expression value)
        {
            return dest switch
            {
                PropertyInfo p when p.GetSetMethod(true) != null => Expression.Assign(Expression.Property(destVar, p), value),
                FieldInfo f => Expression.Assign(Expression.Field(destVar, f), value),
                _ => null
            };
        }

        private static void EnsureDefaultCtor(Type t)
        {
            if (t.IsValueType) return;
            if (t.GetConstructor(Type.EmptyTypes) is null)
                throw new InvalidOperationException($"Destination type {t} must have a public parameterless constructor.");
        }

        private static IEnumerable<MemberInfo> GetSettableMembers(Type t)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
            var props = t.GetProperties(flags).Where(p => p.GetSetMethod(true) != null).Cast<MemberInfo>();
            var fields = t.GetFields(flags).Cast<MemberInfo>();
            return props.Concat(fields);
        }

        private static IEnumerable<MemberInfo> GetReadableMembers(Type t)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
            var props = t.GetProperties(flags).Where(p => p.GetGetMethod(true) != null).Cast<MemberInfo>();
            var fields = t.GetFields(flags).Cast<MemberInfo>();
            return props.Concat(fields);
        }
    }

    internal sealed class ReplaceParameterVisitor : ExpressionVisitor
    {
        private readonly ParameterExpression _from;
        private readonly Expression _to;
        public ReplaceParameterVisitor(ParameterExpression from, Expression to)
        {
            _from = from; _to = to;
        }
        protected override Expression VisitParameter(ParameterExpression node)
            => node == _from ? _to : base.VisitParameter(node);
    }

    internal sealed class MemberMap
    {
        public MemberInfo Destination { get; }
        public bool IsIgnored { get; }
        public LambdaExpression? Resolver { get; }

        private MemberMap(MemberInfo destination, bool isIgnored, LambdaExpression? resolver)
        {
            Destination = destination; IsIgnored = isIgnored; Resolver = resolver;
        }
        public static MemberMap IgnoreMember(MemberInfo destination) => new MemberMap(destination, true, null);
        public static MemberMap Custom(MemberInfo destination, LambdaExpression resolver) => new MemberMap(destination, false, resolver);
    }

    internal enum ReflectionStepKind { Auto, Custom }

    internal sealed class ReflectionStep
    {
        public ReflectionStepKind Kind { get; }
        public MemberInfo Destination { get; }
        public MemberInfo? Source { get; }
        public LambdaExpression? Resolver { get; }
        private ReflectionStep(ReflectionStepKind kind, MemberInfo dest, MemberInfo? src, LambdaExpression? resolver)
        {
            Kind = kind; Destination = dest; Source = src; Resolver = resolver;
        }
        public static ReflectionStep Auto(MemberInfo dest, MemberInfo src) => new ReflectionStep(ReflectionStepKind.Auto, dest, src, null);
        public static ReflectionStep Custom(MemberInfo dest, LambdaExpression resolver) => new ReflectionStep(ReflectionStepKind.Custom, dest, null, resolver);
    }

    internal sealed class ReflectionPlan
    {
        public ReflectionStep[] Steps { get; }
        public ReflectionPlan(ReflectionStep[] steps) { Steps = steps; }
    }

    internal static class MemberInfoExtensions
    {
        public static string NameSafe(this MemberInfo m) => m switch
        {
            PropertyInfo p => p.Name,
            FieldInfo f => f.Name,
            _ => m.Name
        };

        public static Type GetMemberType(this MemberInfo m) => m switch
        {
            PropertyInfo p => p.PropertyType,
            FieldInfo f => f.FieldType,
            _ => throw new NotSupportedException()
        };
    }

    internal sealed class Mapper : IMapper
    {
        private readonly Dictionary<(Type, Type), TypeMap> _maps;
        public Mapper(IEnumerable<TypeMap> maps)
        {
            _maps = maps.ToDictionary(m => (m.SourceType, m.DestinationType));
        }

        public TDest Map<TSrc, TDest>(TSrc source)
        {
            if (!_maps.TryGetValue((typeof(TSrc), typeof(TDest)), out var tm))
                throw new InvalidOperationException($"No map registered for {typeof(TSrc)} -> {typeof(TDest)}.");

            if (tm.Strategy == MappingStrategy.CompiledExpressions)
                return tm.MapCompiled<TSrc, TDest>(source);

            return tm.MapReflection<TSrc, TDest>(source);
        }
    }

    public static class MapperLinqExtensions
    {
        public static List<TDest> MapList<TSrc, TDest>(this IMapper mapper, IEnumerable<TSrc> source)
            => source.Select(mapper.Map<TSrc, TDest>).ToList();
    }

    // ===== Demo domain =====
    public sealed class Address
    {
        public string? City { get; set; }
        public string? State { get; set; }
    }

    public sealed class Person
    {
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public int Age { get; set; }
        public string? Email { get; set; }
        public string Code; // field example
        public Address? Address { get; set; }

        public Person(string code) { Code = code; }
        public Person() : this(string.Empty) { }
    }

    public sealed class PersonDto
    {
        public string? NomeCompleto { get; set; }
        public int Idade { get; set; }
        public string? Email { get; set; }
        public string? Codigo; // field example
        public string? Cidade { get; set; }

        public PersonDto() { Codigo = string.Empty; }
    }
}
