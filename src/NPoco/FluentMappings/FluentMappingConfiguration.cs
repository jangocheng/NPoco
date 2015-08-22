using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using NPoco.RowMappers;

namespace NPoco.FluentMappings
{
    public class FluentConfig
    {
        public FluentConfig(Func<MapperCollection, PocoDataFactory> config)
        {
            Config = config;
        }

        public Func<MapperCollection, PocoDataFactory> Config { get; private set; }
    }

    public class FluentMappingConfiguration
    {
        public static FluentConfig Configure(params IMap[] pocoMaps)
        {
            var mappings = Mappings.BuildMappingsFromMaps(pocoMaps);
            return Configure(mappings);
        }

        public static FluentConfig Configure(Mappings mappings)
        {
            return SetFactory(mappings, null);
        }

        public static FluentConfig Scan(Action<IConventionScanner> scanner)
        {
            var scannerSettings = ProcessSettings(scanner);
            if (scannerSettings.Lazy)
            {
                var lazyPocoMappings = new Mappings();
                return SetFactory(lazyPocoMappings, scanner);
            }
            
            return Configure(CreateMappings(scannerSettings, null));
        }

        private static Mappings CreateMappings(ConventionScannerSettings scannerSettings, Type[] typesOverride)
        {
            var types = typesOverride ?? FindTypes(scannerSettings);
            var config = new Dictionary<Type, TypeDefinition>();
            
            foreach (var type in types)
            {
                var pocoDefn = new TypeDefinition(type)
                {
                    AutoIncrement = scannerSettings.PrimaryKeysAutoIncremented(type),
                    PrimaryKey = scannerSettings.PrimaryKeysNamed(type),
                    TableName = scannerSettings.TablesNamed(type),
                    SequenceName = scannerSettings.SequencesNamed(type),
                    UseOutputClause = scannerSettings.UseOutputClauseWhere(type),
                    ExplicitColumns = true
                };

                foreach (var columnDefinition in GetColumnDefinitions(scannerSettings, type, new List<MemberInfo>()))
                {
                    var key = PocoColumn.GenerateKey(columnDefinition.MemberInfoChain);
                    if (!pocoDefn.ColumnConfiguration.ContainsKey(key))
                        pocoDefn.ColumnConfiguration.Add(key, columnDefinition);
                }
                
                config.Add(type, pocoDefn);
            }

            MergeOverrides(config, scannerSettings.MappingOverrides);

            //if (scannerSettings.OverrideWithAttributes)
            //{
            //    MergeAttributeOverrides(config);
            //}

            var pocoMappings = new Mappings {Config = config};
            return pocoMappings;
        }

        private static IEnumerable<ColumnDefinition> GetColumnDefinitions(ConventionScannerSettings scannerSettings, Type type, List<MemberInfo> memberInfos, bool isReferenceProperty = false)
        {
            var capturedMembers = memberInfos.ToArray();
            foreach (var member in ReflectionUtils.GetFieldsAndPropertiesForClasses(type))
            {
                var complexProperty = scannerSettings.ComplexPropertiesWhere(member);
                var referenceProperty = scannerSettings.ReferencePropertiesWhere(member);
                var dbColumn = scannerSettings.DbColumnWhere(member);

                if ((complexProperty || referenceProperty) && !dbColumn)
                {
                    if (capturedMembers.GroupBy(x => x.GetMemberInfoType()).Any(x => x.Count() >= 2))
                    {
                        continue;
                    }

                    var members = new List<MemberInfo>();
                    members.AddRange(capturedMembers);
                    members.Add(member);

                    var columnDefinitions = GetColumnDefinitions(scannerSettings, member.GetMemberInfoType(), members, referenceProperty).ToList();

                    foreach (var columnDefinition in columnDefinitions)
                    {
                        yield return columnDefinition;
                    }

                    var referenceDbColumnsNamed = scannerSettings.ReferenceDbColumnsNamed(member);

                    yield return new ColumnDefinition()
                    {
                        MemberInfoChain = capturedMembers.Concat(new[] {member}).ToArray(),
                        MemberInfo = member,
                        IsComplexMapping = complexProperty,
                        IsReferenceMember = referenceProperty,
                        ReferenceType = referenceProperty ? ReferenceType.Foreign : ReferenceType.None,
                        ReferenceMember = referenceProperty ? columnDefinitions.Single(x => x.DbColumnName.Equals(referenceDbColumnsNamed, StringComparison.InvariantCultureIgnoreCase)).MemberInfo : null,
                        DbColumnName = referenceProperty ? referenceDbColumnsNamed : null,
                    };
                }
                else
                {
                    var columnDefinition = new ColumnDefinition();
                    columnDefinition.MemberInfoChain = capturedMembers.Concat(new[] {member}).ToArray();
                    columnDefinition.MemberInfo = member;

                    var prefixProperty = isReferenceProperty ? Enumerable.Empty<string>() : capturedMembers.Select(x => scannerSettings.DbColumnsNamed(x));
                    columnDefinition.DbColumnName = string.Join(PocoData.Separator, prefixProperty.Concat(new[] { scannerSettings.DbColumnsNamed(member) }).ToArray());

                    columnDefinition.DbColumnAlias = scannerSettings.AliasNamed(member);
                    columnDefinition.IgnoreColumn = scannerSettings.IgnorePropertiesWhere.Any(x => x.Invoke(member));
                    columnDefinition.DbColumnType = scannerSettings.DbColumnTypesAs(member);
                    columnDefinition.ResultColumn = scannerSettings.ResultPropertiesWhere(member);
                    columnDefinition.ComputedColumn = scannerSettings.ComputedPropertiesWhere(member);
                    columnDefinition.VersionColumn = scannerSettings.VersionPropertiesWhere(member);
                    columnDefinition.VersionColumnType = scannerSettings.VersionColumnTypeAs(member);
                    columnDefinition.ForceUtc = scannerSettings.ForceDateTimesToUtcWhere(member);
                    columnDefinition.Serialized = scannerSettings.SerializedWhere(member);
                    yield return columnDefinition;
                }
            }
        }

        private static ConventionScannerSettings ProcessSettings(Action<IConventionScanner> scanner)
        {
            var defaultScannerSettings = new ConventionScannerSettings
            {
                PrimaryKeysAutoIncremented = x => true,
                PrimaryKeysNamed = x => "ID",
                TablesNamed = x => x.Name,
                DbColumnsNamed = x => x.Name,
                AliasNamed = x => null,
                DbColumnTypesAs = x => null,
                ResultPropertiesWhere = x => false,
                VersionPropertiesWhere = x => false,
                VersionColumnTypeAs = x => VersionColumnType.Number,
                ComputedPropertiesWhere = x => false,
                ForceDateTimesToUtcWhere = x => true,
                ReferencePropertiesWhere = x => x.GetMemberInfoType().IsAClass() && Attribute.GetCustomAttributes(x, typeof(ReferenceAttribute)).Any(),
                ComplexPropertiesWhere = x => x.GetMemberInfoType().IsAClass() && Attribute.GetCustomAttributes(x, typeof(ComplexMappingAttribute)).Any(),
                ReferenceDbColumnsNamed = x => x.Name + "ID",
                SequencesNamed = x => null,
                UseOutputClauseWhere = x => false,
                SerializedWhere = x => Attribute.GetCustomAttributes(x, typeof(SerializedColumnAttribute)).Any(),
                DbColumnWhere = x => Attribute.GetCustomAttributes(x, typeof(ColumnAttribute)).Any(),
                Lazy = false
            };
            scanner.Invoke(new ConventionScanner(defaultScannerSettings));
            return defaultScannerSettings;
        }

        private static IEnumerable<Type> FindTypes(ConventionScannerSettings scannerSettings)
        {
            if (scannerSettings.TheCallingAssembly)
                scannerSettings.Assemblies.Add(FindTheCallingAssembly());

            var types = scannerSettings.Assemblies
                .SelectMany(x => x.GetExportedTypes())
                .Where(x => scannerSettings.IncludeTypes.All(y => y.Invoke(x)))
                .Where(x => !x.IsNested && !typeof (Map<>).IsAssignableFrom(x) && !typeof (Mappings).IsAssignableFrom(x));
            return types;
        }

        private static void MergeAttributeOverrides(Dictionary<Type, TypeDefinition> config)
        {
            foreach (var typeDefinition in config)
            {
                var tableInfo = TableInfo.FromPoco(typeDefinition.Key);
                typeDefinition.Value.TableName = tableInfo.TableName;
                typeDefinition.Value.PrimaryKey = tableInfo.PrimaryKey;
                typeDefinition.Value.SequenceName = tableInfo.SequenceName;
                typeDefinition.Value.AutoIncrement = tableInfo.AutoIncrement;
                typeDefinition.Value.UseOutputClause = tableInfo.UseOutputClause;

                foreach (var columnDefinition in typeDefinition.Value.ColumnConfiguration)
                {
                    var columnInfo = ColumnInfo.FromMemberInfo(columnDefinition.Value.MemberInfo);
                    columnDefinition.Value.DbColumnName = columnInfo.ColumnName;
                    columnDefinition.Value.DbColumnAlias = columnInfo.ColumnAlias;
                    columnDefinition.Value.DbColumnType = columnInfo.ColumnType;
                    columnDefinition.Value.IgnoreColumn = columnInfo.IgnoreColumn;
                    columnDefinition.Value.ResultColumn = columnInfo.ResultColumn;
                    columnDefinition.Value.ComputedColumn = columnInfo.ComputedColumn;
                    columnDefinition.Value.VersionColumn = columnInfo.VersionColumn;
                    columnDefinition.Value.VersionColumnType = columnInfo.VersionColumnType;
                    columnDefinition.Value.ForceUtc = columnInfo.ForceToUtc;
                    columnDefinition.Value.Serialized = columnInfo.SerializedColumn;
                }
            }
        }

        private static void MergeOverrides(Dictionary<Type, TypeDefinition> config, Mappings overrideMappings)
        {
            if (overrideMappings == null)
                return;

            foreach (var overrideTypeDefinition in overrideMappings.Config)
            {
                if (!config.ContainsKey(overrideTypeDefinition.Key))
                    continue;

                var convTableDefinition = config[overrideTypeDefinition.Key];

                convTableDefinition.PrimaryKey = overrideTypeDefinition.Value.PrimaryKey ?? convTableDefinition.PrimaryKey;
                convTableDefinition.SequenceName = overrideTypeDefinition.Value.SequenceName ?? convTableDefinition.SequenceName;
                convTableDefinition.TableName = overrideTypeDefinition.Value.TableName ?? convTableDefinition.TableName;
                convTableDefinition.AutoIncrement = overrideTypeDefinition.Value.AutoIncrement ?? convTableDefinition.AutoIncrement;
                convTableDefinition.ExplicitColumns = overrideTypeDefinition.Value.ExplicitColumns ?? convTableDefinition.ExplicitColumns;
                convTableDefinition.UseOutputClause = overrideTypeDefinition.Value.UseOutputClause ?? convTableDefinition.UseOutputClause;

                foreach (var overrideColumnDefinition in overrideMappings.Config[overrideTypeDefinition.Key].ColumnConfiguration)
                {
                    var convColDefinition = convTableDefinition.ColumnConfiguration[overrideColumnDefinition.Key];

                    convColDefinition.DbColumnName = overrideColumnDefinition.Value.DbColumnName ?? convColDefinition.DbColumnName;
                    convColDefinition.DbColumnAlias = overrideColumnDefinition.Value.DbColumnAlias ?? convColDefinition.DbColumnAlias;
                    convColDefinition.DbColumnType = overrideColumnDefinition.Value.DbColumnType ?? convColDefinition.DbColumnType;
                    convColDefinition.IgnoreColumn = overrideColumnDefinition.Value.IgnoreColumn ?? convColDefinition.IgnoreColumn;
                    convColDefinition.ResultColumn = overrideColumnDefinition.Value.ResultColumn ?? convColDefinition.ResultColumn;
                    convColDefinition.ComputedColumn = overrideColumnDefinition.Value.ComputedColumn ?? convColDefinition.ComputedColumn;
                    convColDefinition.VersionColumn = overrideColumnDefinition.Value.VersionColumn ?? convColDefinition.VersionColumn;
                    convColDefinition.VersionColumnType = overrideColumnDefinition.Value.VersionColumnType ?? convColDefinition.VersionColumnType;
                    convColDefinition.MemberInfo = overrideColumnDefinition.Value.MemberInfo ?? convColDefinition.MemberInfo;
                    convColDefinition.ForceUtc = overrideColumnDefinition.Value.ForceUtc ?? convColDefinition.ForceUtc;
                    convColDefinition.IsReferenceMember = overrideColumnDefinition.Value.IsReferenceMember ?? convColDefinition.IsReferenceMember;
                    convColDefinition.ReferenceMember = overrideColumnDefinition.Value.ReferenceMember ?? convColDefinition.ReferenceMember;
                    convColDefinition.ReferenceType = overrideColumnDefinition.Value.ReferenceType ?? convColDefinition.ReferenceType;
                    convColDefinition.Serialized = overrideColumnDefinition.Value.Serialized ?? convColDefinition.Serialized;
                }
            }
        }

        private static FluentConfig SetFactory(Mappings mappings, Action<IConventionScanner> scanner)
        {
            var maps = mappings;
            var scana = scanner;
            return new FluentConfig(mapper => new PocoDataFactory((t, pocoDataFactory) =>
            {
                if (maps != null)
                {
                    if (maps.Config.ContainsKey(t))
                    {
                        return new FluentMappingsPocoDataBuilder(t, mappings, mapper, pocoDataFactory).Init();
                    }

                    if (scana != null)
                    {
                        var settings = ProcessSettings(scana);
                        var typeMapping = CreateMappings(settings, new[] { t });
                        return new FluentMappingsPocoDataBuilder(t, typeMapping, mapper, pocoDataFactory).Init();
                    }
                }
                return new PocoDataBuilder(t, mapper, pocoDataFactory).Init();
            }));
        }

        // Helper method if code is in seperate assembly
        private static Assembly FindTheCallingAssembly()
        {
            if (!typeof(FluentMappingConfiguration).Assembly.FullName.StartsWith("NPoco,"))
                return Assembly.GetCallingAssembly();

            var trace = new StackTrace(false);

            Assembly thisAssembly = Assembly.GetExecutingAssembly();
            Assembly callingAssembly = null;
            for (int i = 0; i < trace.FrameCount; i++)
            {
                StackFrame frame = trace.GetFrame(i);
                Assembly assembly = frame.GetMethod().DeclaringType.Assembly;
                if (assembly != thisAssembly)
                {
                    callingAssembly = assembly;
                    break;
                }
            }
            return callingAssembly;
        }
    }
}