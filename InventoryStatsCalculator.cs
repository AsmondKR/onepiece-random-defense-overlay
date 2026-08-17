using System.Globalization;

namespace OrandOverlay;

public sealed record InventoryStatSummary(
    double Stun,
    double Slow,
    double TriggeredSlow,
    double ArmorReduction,
    double TriggeredArmorReduction,
    double StackingArmorReduction,
    double SingleArmorReduction,
    double AttackBoost,
    double TriggeredAttackBoost,
    double AttackSpeed,
    double HealthRegen,
    double ManaRegen,
    int ArmorBreakProviders,
    int BossControlProviders,
    int BerserkControlProviders,
    int AirMovementProviders,
    int TeleportProviders,
    int BurgessProviders,
    int SingleDamageProviders = 0,
    int FinisherDamageProviders = 0,
    double MagicArmorReduction = 0,
    double MagicAmp = 0)
{
    public double TotalSlow => Slow + TriggeredSlow;
    public double TotalArmorReduction => ArmorReduction + TriggeredArmorReduction + StackingArmorReduction;
    public double TotalAttackBoost => AttackBoost + TriggeredAttackBoost;
}

public sealed class InventoryStatsCalculator(DataCatalog catalog)
{
    public InventoryStatSummary Calculate(IEnumerable<InventoryEntry> inventory)
    {
        var stun = 0d;
        var slow = 0d;
        var triggeredSlow = 0d;
        var armor = 0d;
        var triggeredArmor = 0d;
        var stackingArmor = 0d;
        var singleArmor = 0d;
        var attack = 0d;
        var triggeredAttack = 0d;
        var attackSpeed = 0d;
        var healthRegen = 0d;
        var manaRegen = 0d;
        var magicArmor = 0d;
        var magicAmp = 0d;
        var armorBreakProviders = 0;
        var bossProviders = 0;
        var berserkProviders = 0;
        var airMovementProviders = 0;
        var teleportProviders = 0;
        var burgessProviders = 0;
        var singleDamageProviders = 0;
        var finisherDamageProviders = 0;

        foreach (var entry in inventory.Where(entry => entry.Count > 0))
        {
            var unit = catalog.Unit(entry.UnitId);
            var count = entry.Count;
            stun += Value(unit, "스턴") * count;
            slow += Value(unit, "이동속도 감소") * count;
            triggeredSlow += Value(unit, "발동이동속도 감소") * count;
            armor += Value(unit, "방어력 감소") * count;
            triggeredArmor += Value(unit, "발동방어력 감소") * count;
            stackingArmor += Value(unit, "중첩방어력 감소") * count;
            singleArmor += Value(unit, "단일방어력 감소") * count;
            attack += Value(unit, "공격력 증가") * count;
            triggeredAttack += Value(unit, "발동공격력 증가") * count;
            attackSpeed += Value(unit, "공격속도 증가") * count;
            healthRegen += Value(unit, "체력 재생") * count;
            manaRegen += Value(unit, "마나 재생") * count;
            magicArmor += Value(unit, "마법방어력 감소") * count;
            magicAmp += Value(unit, "마법데미지 증폭") * count;

            if (Has(unit, "아머브레이크", "단일아머브레이크")) armorBreakProviders += count;
            if (Has(unit, "보스 잡기")) bossProviders += count;
            if (Has(unit, "광폭화 잡기", "광폭화")) berserkProviders += count;
            if (Has(unit, "공중이동")) airMovementProviders += count;
            if (Has(unit, "순간이동")) teleportProviders += count;
            if (Has(unit, "바제스")) burgessProviders += count;
            // 마딜 밸런스(고인물 검증): 상위가 단일이면 끝딜을, 끝딜이면 단일을
            // 보완해야 하므로 두 딜 유형의 보유 기수를 따로 센다.
            if (Has(unit, "단일")) singleDamageProviders += count;
            if (Has(unit, "끝딜")) finisherDamageProviders += count;
        }

        return new InventoryStatSummary(stun, slow, triggeredSlow, armor, triggeredArmor,
            stackingArmor, singleArmor, attack, triggeredAttack, attackSpeed, healthRegen, manaRegen,
            armorBreakProviders, bossProviders, berserkProviders, airMovementProviders,
            teleportProviders, burgessProviders, singleDamageProviders, finisherDamageProviders,
            magicArmor, magicAmp);
    }

    private static double Value(UnitDefinition unit, params string[] names) => unit.OfficialAbilities
        .Where(ability => names.Contains(ability.Name, StringComparer.Ordinal))
        .Sum(ability => double.TryParse(ability.DisplayValue, NumberStyles.Float,
            CultureInfo.InvariantCulture, out var value) ? value : 0);

    private static bool Has(UnitDefinition unit, params string[] names) => unit.OfficialAbilities.Any(ability =>
        names.Contains(ability.Name, StringComparer.Ordinal) &&
        !ability.DisplayValue.Equals("불가", StringComparison.OrdinalIgnoreCase));
}
