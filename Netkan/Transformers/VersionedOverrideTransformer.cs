using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using log4net;
using Newtonsoft.Json.Linq;

using CKAN.NetKAN.Model;
using CKAN.Versioning;

namespace CKAN.NetKAN.Transformers
{
    /// <summary>
    /// An <see cref="ITransformer"/> that overrides properties based on the version.
    /// </summary>
    internal sealed class VersionedOverrideTransformer : ITransformer
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(VersionedOverrideTransformer));

        private readonly List<string?> _before;
        private readonly List<string?> _after;

        public string Name => "versioned_override";

        public VersionedOverrideTransformer(IEnumerable<string?> before,
                                            IEnumerable<string?> after)
        {
            _before = new List<string?>(before);
            _after  = new List<string?>(after);
        }

        public void AddBefore(string before)
        {
            _before.Add(before);
        }

        public void AddAfter(string after)
        {
            _after.Add(after);
        }

        public IEnumerable<Metadata> Transform(Metadata metadata, TransformOptions opts)
        {
            if (metadata.AllJson.TryGetValue("x_netkan_override", out JToken? overrideList))
            {
                var json = metadata.Json();
                Log.InfoFormat("Executing override transformation with {0}", metadata.Kref);
                Log.DebugFormat("Input metadata:{0}{1}", Environment.NewLine, metadata.AllJson);

                // There's an override section, process them

                Log.DebugFormat("Override section:{0}{1}", Environment.NewLine, overrideList);

                if (overrideList.Type == JTokenType.Array)
                {
                    // Sweet! We have an override. Let's walk through and see if we can find
                    // an applicable section for us.
                    foreach (var overrideStanza in overrideList)
                    {
                        ProcessOverrideStanza((JObject)overrideStanza, json);
                    }

                    Log.DebugFormat("Transformed metadata:{0}{1}", Environment.NewLine, json);

                    yield return new Metadata(json);
                    yield break;
                }
                else
                {
                    throw new Kraken(
                        string.Format(
                            "x_netkan_override expects a list of overrides, found: {0}",
                            overrideList));
                }
            }
            yield return metadata;
        }

        /// <summary>
        /// Processes an individual override stanza, altering the object's
        /// metadata in the process if applicable.
        /// </summary>
        private void ProcessOverrideStanza(JObject overrideStanza, JObject metadata)
        {
            string? before = null;
            string? after  = null;

            if (overrideStanza.TryGetValue("before", out JToken? jBefore))
            {
                if (jBefore is JValue val)
                {
                    before = val.ToString();
                }
                else
                {
                    throw new Kraken("override before property must be a string");
                }
            }

            if (overrideStanza.TryGetValue("after", out JToken? jAfter))
            {
                if (jAfter is JValue val)
                {
                    after = val.ToString();
                }
                else
                {
                    throw new Kraken("override after property must be a string");
                }
            }

            if (_before.Contains(before) || _after.Contains(after))
            {
                Log.InfoFormat("Processing override: {0}", overrideStanza);

                if (!overrideStanza.TryGetValue("version", out JToken? stanzaConstraints))
                {
                    throw new Kraken(
                        string.Format(
                            "Can't find version in override stanza {0}",
                            overrideStanza));
                }

                IEnumerable<string> constraints;

                // First let's get our constraints into a list of strings.

                if (stanzaConstraints is JValue)
                {
                    constraints = new List<string> { stanzaConstraints.ToString() };
                }
                else if (stanzaConstraints is JArray array)
                {
                    // Pop the constraints in 'constraints'
                    constraints = array.Values().Select(x => x.ToString());
                }
                else
                {
                    throw new Kraken(
                        string.Format("Totally unexpected x_netkan_override - {0}", stanzaConstraints));
                }

                // If the constraints don't apply, then do nothing.
                if (metadata["version"]?.ToString() is string s
                    && !ConstraintsApply(constraints, new ModuleVersion(s)))
                {
                    return;
                }

                // All the constraints pass; let's replace the metadata we have with what's
                // in the override.

                if (overrideStanza.TryGetValue("override", out JToken? overrideBlock)
                    && overrideBlock is JObject overrides)
                {
                    if (gameVersionProperties.Any(overrides.ContainsKey))
                    {
                        GameVersion.SetJsonCompatibility(
                            metadata,
                            (string?)overrides["ksp_version"] is string v1
                                ? GameVersion.Parse(v1)
                                : null,
                            (string?)overrides["ksp_version_min"] is string v2
                                ? GameVersion.Parse(v2)
                                : null,
                            (string?)overrides["ksp_version_max"] is string v3
                                ? GameVersion.Parse(v3)
                                : null
                        );
                        foreach (var p in gameVersionProperties)
                        {
                            overrides.Remove(p);
                        }
                    }
                    foreach (var property in overrides.Properties())
                    {
                        metadata[property.Name] = property.Value;
                    }
                }

                // And let's delete anything that needs deleting.

                if (overrideStanza.TryGetValue("delete", out JToken? deleteList))
                {
                    foreach (string key in ((JArray)deleteList).Select(v => (string?)v)
                                                               .OfType<string>())
                    {
                        metadata.Remove(key);
                    }
                }
            }
        }

        private readonly string[] gameVersionProperties = new string[]
        {
            "ksp_version", "ksp_version_min", "ksp_version_max"
        };

        /// <summary>
        /// Walks through a list of constraints, and returns true if they're all satisifed
        /// for the mod version we're examining.
        /// </summary>
        private static bool ConstraintsApply(IEnumerable<string> constraints, ModuleVersion version)
        {
            foreach (var constraint in constraints)
            {
                var match = Regex.Match(
                    constraint,
                    @"^(?<op> [<>=]*) \s* (?<version> .*)$",
                    RegexOptions.IgnorePatternWhitespace);

                if (!match.Success)
                {
                    throw new Kraken(
                        string.Format("Unable to parse x_netkan_override - {0}", constraint));
                }

                var op = match.Groups["op"].Value;
                var desiredVersion = new ModuleVersion(match.Groups["version"].Value);

                // This contstraint failed. This stanza is not for us.
                if (!ConstraintPasses(op, version, desiredVersion))
                {
                    return false;
                }
            }

            // All the constraints passed! We want to apply this stanza!
            return true;
        }

        /// <summary>
        /// Returns whether the given constraint matches the desired version
        /// for the mod we're processing.
        /// </summary>
        private static bool ConstraintPasses(string op, ModuleVersion version, ModuleVersion desiredVersion)
        {
            switch (op)
            {
                case "":
                case "=":
                    return version.IsEqualTo(desiredVersion);

                case "<":
                    return version.IsLessThan(desiredVersion);

                case ">":
                    return version.IsGreaterThan(desiredVersion);

                case "<=":
                    return version.CompareTo(desiredVersion) <= 0;

                case ">=":
                    return version.CompareTo(desiredVersion) >= 0;

                default:
                    throw new Kraken(
                        string.Format("Unknown x_netkan_override comparator: {0}", op));
            }
        }
    }
}
