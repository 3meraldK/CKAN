using System;
using System.Collections.Generic;
using CKAN.NetKAN.Model;
using log4net;

namespace CKAN.NetKAN.Transformers
{
    /// <summary>
    /// An <see cref="ITransformer"/> that adds a property to indicate the program responsible for generating the
    /// metadata.
    /// </summary>
    internal sealed class GeneratedByTransformer : ITransformer
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(GeneratedByTransformer));

        public string Name => "generated_by";

        public IEnumerable<Metadata> Transform(Metadata metadata, TransformOptions opts)
        {
            var json = metadata.Json();

            Log.Debug("Executing generated by transformation");
            Log.DebugFormat("Input metadata:{0}{1}", Environment.NewLine, metadata.AllJson);

            json["x_generated_by"] = "netkan";

            Log.DebugFormat("Transformed metadata:{0}{1}", Environment.NewLine, json);

            yield return new Metadata(json);
        }
    }
}
