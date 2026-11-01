using Siemens.Engineering;
using System;
using System.Collections.Generic;

namespace TiaMcpServer.ModelContextProtocol
{
    // Explicit public V21 API only: never enumerate SDK properties or follow parents.
    public static class SiemensAttributeProjection
    {
        public static bool TryConvert(object value, out object? converted)
        {
            if (value is MultilingualText text)
            {
                var translations = new Dictionary<string, object?>(StringComparer.Ordinal);
                int count = 0;
                foreach (var item in text.Items)
                {
                    if (++count > AttributeProjection.MaxItems)
                        throw new NotSupportedException("Multilingual text exceeds 256 entries.");
                    var culture = item.Language.Culture.Name;
                    // Add deliberately rejects duplicate cultures instead of losing text.
                    translations.Add(culture, item.Text);
                }
                converted = translations;
                return true;
            }
            if (value is Language language)
            {
                converted = language.Culture.Name;
                return true;
            }
            if (value is global::Siemens.Engineering.HW.DeviceItemAssociation items)
            {
                var references = new List<object?>();
                foreach (var item in items)
                {
                    if (references.Count >= AttributeProjection.MaxItems)
                        throw new NotSupportedException("Device item association exceeds 256 entries.");
                    references.Add(item);
                }
                converted = references;
                return true;
            }
            if (value is global::Siemens.Engineering.Security.Certificate certificate)
            {
                converted = new Dictionary<string, object?>
                {
                    ["Id"] = certificate.Id,
                    ["SubjectCommonName"] = certificate.SubjectCommonName,
                    ["ValidUntil"] = certificate.ValidUntil,
                    ["HasPrivateKey"] = certificate.HasPrivateKey
                };
                return true;
            }
            if (value is global::Siemens.Engineering.HW.CustomDataTypes.TableData table)
            {
                var rows = new List<object?>();
                foreach (IEngineeringObject row in table.Rows)
                {
                    if (rows.Count >= AttributeProjection.MaxItems)
                        throw new NotSupportedException("Table data exceeds 256 rows.");
                    var cells = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var attribute in row.GetAttributeInfos())
                    {
                        if (cells.Count >= AttributeProjection.MaxItems)
                            throw new NotSupportedException("Table row exceeds 256 attributes.");
                        if ((attribute.AccessMode & EngineeringAttributeAccessMode.Read) == 0)
                            throw new NotSupportedException($"Table attribute '{attribute.Name}' is not readable.");
                        cells.Add(attribute.Name, row.GetAttribute(attribute.Name));
                    }
                    rows.Add(cells);
                }
                converted = new Dictionary<string, object?> { ["Rows"] = rows };
                return true;
            }
            if (value is IEngineeringObject reference)
            {
                // DeviceItemImpl and other SDK implementations expose this through
                // IEngineeringObject. Read only Name, never Parent or compositions.
                var type = value.GetType().FullName;
                object? name;
                try { name = reference.GetAttribute("Name"); }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Cannot read Name for engineering reference '{type}'.", ex);
                }
                if (!(name is string readableName) || string.IsNullOrWhiteSpace(readableName))
                    throw new NotSupportedException($"Engineering reference '{type}' has no readable non-empty string Name.");
                converted = new Dictionary<string, object?>
                {
                    ["Name"] = readableName,
                    ["Type"] = type
                };
                return true;
            }
            // SDK associations/compositions implementing IList are bounded and
            // enumerated by AttributeProjection; each engineering child uses this adapter.
            converted = null;
            return false;
        }
    }
}
