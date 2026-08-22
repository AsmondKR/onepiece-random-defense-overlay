using System.Text.Json;
using Xunit;

namespace OrandOverlay.Tests;

public sealed class RecipeCatalogParityTests
{
    [Fact]
    public void EveryLoadedRecipeMatchesCurrentBundledTmoRecipe()
    {
        var catalog = new DataCatalog();
        catalog.Load();
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "tmo-unit-catalog.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var units = document.RootElement.GetProperty("units").EnumerateArray().ToList();
        Assert.True(units.Count >= 250, $"공식 유닛 수가 비정상적으로 적습니다: {units.Count}");

        var mismatches = new List<string>();
        foreach (var source in units)
        {
            var rawcode = source.GetProperty("rawcode").GetString()!;
            var tier = source.GetProperty("tier").GetString() ?? "";
            // 세라핌은 번들 데이터에 레시피가 비어 있고 앱이 호스트 재료+그린블러드를
            // 주입하므로(SeraphimMaterialRawcodes 특례) 저장값 대조에서 제외한다.
            if (tier.Split('[', 2)[0].Trim() == "세라핌") continue;
            var expected = source.GetProperty("recipe").EnumerateArray()
                .Where(item => item.GetProperty("count").GetInt32() > 0)
                .GroupBy(item => ResolveIngredientUnitId(catalog,
                    item.GetProperty("id").GetString()!),
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key,
                    group => group.Sum(item => item.GetProperty("count").GetInt32()),
                    StringComparer.OrdinalIgnoreCase);
            var actual = catalog.Unit("rawcode:" + rawcode).Recipe;
            var equal = expected.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(actual.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase));
            if (!equal)
                mismatches.Add($"{rawcode}: 예상=[{string.Join(",", expected.Select(p => p.Key + "x" + p.Value))}] " +
                               $"실제=[{string.Join(",", actual.Select(p => p.Key + "x" + p.Value))}]");
        }
        Assert.True(mismatches.Count == 0,
            "번들 카탈로그와 로드된 레시피가 일치하지 않습니다:\n" + string.Join("\n", mismatches));
    }

    // RecipeFor와 동일한 해석: 재료 rawcode는 그 코드를 보유한 데모 유닛 ID로,
    // 없으면 rawcode: 접두사 형태로 남는다.
    private static string ResolveIngredientUnitId(DataCatalog catalog, string ingredientRawcode)
    {
        var owner = catalog.AllUnits.FirstOrDefault(unit =>
            unit.Tags.All(tag => tag != "rawcode-catalog") &&
            unit.Rawcodes.Contains(ingredientRawcode, StringComparer.Ordinal));
        return owner?.Id ?? "rawcode:" + ingredientRawcode;
    }
}
