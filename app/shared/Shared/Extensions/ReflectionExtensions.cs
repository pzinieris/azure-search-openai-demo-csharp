using System.Reflection;
using System.Text.Json.Serialization;

namespace Shared.Extensions;
public static class ReflectionExtensions
{
    public static string GetJsonPropertyNameAttributeValue<T>(this T obj, string targetPropertyName)
        where T : class
    {
        return targetPropertyName.GetJsonPropertyNameAttributeValue(typeof(T));
    }

    public static string GetJsonPropertyNameAttributeValue(this string targetPropertyName, Type objType)
    {
        PropertyInfo[] properties = objType.GetProperties();
        foreach (PropertyInfo property in properties)
        {
            if (property.Name != targetPropertyName)
            {
                continue;
            }

            object[] attributes = property.GetCustomAttributes(true);
            foreach (object attribute in attributes)
            {
                if (attribute is JsonPropertyNameAttribute propNameAttr)
                {
                    return propNameAttr.Name;
                }
            }
        }

        throw new ArgumentException("""TargetPropertyName: '{TargetPropertyName}' does not exists in the object""", targetPropertyName);
    }
}
