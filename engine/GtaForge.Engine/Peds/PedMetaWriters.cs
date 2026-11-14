using GtaForge.Engine.Gta;

namespace GtaForge.Engine.Peds;

public sealed record PedDrawable(PedComponents.Comp Comp, int Index, int TextureCount);

/// <summary>Ped variation metadata (.ymt, PSO CPedVariationInfo).</summary>
public static class PedYmtWriter
{
    public static string BuildXml(IReadOnlyList<PedDrawable> drawables)
    {
        var byComp = drawables.GroupBy(d => d.Comp.Index).OrderBy(g => g.Key).ToList();
        var avail = Enumerable.Repeat(255, 12).ToArray();
        for (int i = 0; i < byComp.Count; i++) avail[byComp[i].Key] = i;

        var x = new Xw();
        x.Open("CPedVariationInfo");
        x.Value("bHasTexVariations", drawables.Any(d => d.TextureCount > 1));
        x.Value("bHasDrawblVariations", byComp.Any(g => g.Count() > 1));
        x.Value("bHasLowLODs", false);
        x.Value("bIsSuperLOD", false);
        x.Text("availComp", string.Join(' ', avail));
        x.Open("aComponentData3");
        foreach (var g in byComp)
        {
            x.Open("Item");
            x.Value("numAvailTex", g.Sum(d => d.TextureCount));
            x.Open("aDrawblData3");
            foreach (var d in g.OrderBy(d => d.Index))
            {
                x.Open("Item");
                x.Value("propMask", 1);
                x.Value("numAlternatives", 0);
                x.Open("aTexData");
                for (int t = 0; t < d.TextureCount; t++)
                {
                    x.Open("Item");
                    x.Value("texId", 0); // 0 = "uni" texture race
                    x.Value("distribution", 255);
                    x.Close("Item");
                }
                x.Close("aTexData");
                x.Open("clothData");
                x.Value("ownsCloth", false);
                x.Close("clothData");
                x.Close("Item");
            }
            x.Close("aDrawblData3");
            x.Close("Item");
        }
        x.Close("aComponentData3");
        x.Empty("aSelectionSets");
        x.Open("compInfos");
        foreach (var g in byComp)
            foreach (var d in g.OrderBy(d => d.Index))
            {
                x.Open("Item");
                x.Text("hash_2FD08CEF", "none");      // audio id
                x.Text("hash_FC507D28", "none");      // audio id 2
                x.Text("hash_07AE529D", "0 0 0 0 0"); // expression mods (last = heel height offset)
                x.Value("flags", 0);
                x.Text("inclusions", "0");
                x.Text("exclusions", "0");
                x.Text("hash_6032815C", d.Comp.Enum); // vfx components
                x.Value("hash_7E103C8B", 0);          // flags
                x.Value("hash_D12F579D", d.Comp.Index); // component index
                x.Value("hash_FA1F27BF", d.Index);    // drawable index
                x.Close("Item");
            }
        x.Close("compInfos");
        x.Open("propInfo");
        x.Value("numAvailProps", 0);
        x.Empty("aPropMetaData");
        x.Empty("aAnchors");
        x.Close("propInfo");
        x.Text("dlcName", "");
        x.Close("CPedVariationInfo");
        return x.ToString();
    }

    public static byte[] Build(IReadOnlyList<PedDrawable> drawables) => GtaIO.PsoFromXml(BuildXml(drawables));
}

/// <summary>peds.meta entry (CPedModelInfo__InitData) for an addon ped.</summary>
public static class PedsMetaWriter
{
    // Values follow shipped addon-ped peds.meta entries (male: S_M_Y_Casino_01 style, female: S_F_Y_Casino_01 style).
    public static string Build(string name, PedRequest req)
    {
        bool female = req.PedType is "CIVFEMALE" or "COP_FEMALE" or "MEDIC_FEMALE";
        string g = female ? "f" : "m";
        string G = female ? "F" : "M";
        return $"""
<?xml version="1.0" encoding="UTF-8"?>
<CPedModelInfo__InitDataList>
  <InitDatas>
    <Item>
      <Name>{Xw.Esc(name)}</Name>
      <PropsName>null</PropsName>
      <ClipDictionaryName>move_{g}@generic</ClipDictionaryName>
      <BlendShapeFileName>null</BlendShapeFileName>
      <ExpressionSetName>{(female ? "null" : "expr_set_ambient_male")}</ExpressionSetName>
      <ExpressionDictionaryName>null</ExpressionDictionaryName>
      <ExpressionName>null</ExpressionName>
      <Pedtype>{Xw.Esc(req.PedType)}</Pedtype>
      <MovementClipSet>move_{g}@generic</MovementClipSet>
      <StrafeClipSet>move_ped_strafing</StrafeClipSet>
      <MovementToStrafeClipSet>move_ped_to_strafe</MovementToStrafeClipSet>
      <InjuredStrafeClipSet>move_strafe_injured</InjuredStrafeClipSet>
      <FullBodyDamageClipSet>dam_ko</FullBodyDamageClipSet>
      <AdditiveDamageClipSet>dam_ad</AdditiveDamageClipSet>
      <DefaultGestureClipSet>ANIM_GROUP_GESTURE_{G}_GENERIC</DefaultGestureClipSet>
      <FacialClipsetGroupName>facial_clipset_group_gen_{(female ? "female" : "male")}</FacialClipsetGroupName>
      <DefaultVisemeClipSet>ANIM_GROUP_VISEMES_{G}_LO</DefaultVisemeClipSet>
      <SidestepClipSet>CLIP_SET_ID_INVALID</SidestepClipSet>
      <PoseMatcherName>Male</PoseMatcherName>
      <PoseMatcherProneName>Male_prone</PoseMatcherProneName>
      <GetupSetHash>NMBS_SLOW_GETUPS</GetupSetHash>
      <CreatureMetadataName>null</CreatureMetadataName>
      <DecisionMakerName>DEFAULT</DecisionMakerName>
      <MotionTaskDataSetName>STANDARD_PED</MotionTaskDataSetName>
      <DefaultTaskDataSetName>STANDARD_PED</DefaultTaskDataSetName>
      <PedCapsuleName>STANDARD_{(female ? "FEMALE" : "MALE")}</PedCapsuleName>
      <PedLayoutName />
      <PedComponentSetName />
      <PedComponentClothName />
      <PedIKSettingsName />
      <TaskDataName />
      <IsStreamedGfx value="false" />
      <AmbulanceShouldRespondTo value="true" />
      <CanRideBikeWithNoHelmet value="false" />
      <CanSpawnInCar value="true" />
      <IsHeadBlendPed value="false" />
      <bOnlyBulkyItemVariations value="false" />
      <RelationshipGroup>{(female ? "CIVFEMALE" : "CIVMALE")}</RelationshipGroup>
      <NavCapabilitiesName>STANDARD_PED</NavCapabilitiesName>
      <PerceptionInfo>DEFAULT_PERCEPTION</PerceptionInfo>
      <DefaultBrawlingStyle>BS_AI</DefaultBrawlingStyle>
      <DefaultUnarmedWeapon>WEAPON_UNARMED</DefaultUnarmedWeapon>
      <Personality>{(female ? "YOUNGRICHWOMAN" : "SERVICEMALES")}</Personality>
      <CombatInfo>DEFAULT</CombatInfo>
      <VfxInfoName>VFXPEDINFO_HUMAN_GENERIC</VfxInfoName>
      <AmbientClipsForFlee>FLEE</AmbientClipsForFlee>
      <Radio1>RADIO_GENRE_OFF</Radio1>
      <Radio2>RADIO_GENRE_OFF</Radio2>
      <FUpOffset value="0.000000" />
      <RUpOffset value="0.000000" />
      <FFrontOffset value="0.000000" />
      <RFrontOffset value="0.147000" />
      <MinActivationImpulse value="20.000000" />
      <Stubble value="0.000000" />
      <HDDist value="3.000000" />
      <TargetingThreatModifier value="1.000000" />
      <KilledPerceptionRangeModifer value="-1.000000" />
      <Sexiness />
      <Age value="0" />
      <MaxPassengersInCar value="0" />
      <ExternallyDrivenDOFs />
      <PedVoiceGroup />
      <AnimalAudioObject />
      <AbilityType>SAT_NONE</AbilityType>
      <ThermalBehaviour>TB_WARM</ThermalBehaviour>
      <SuperlodType>SLOD_HUMAN</SuperlodType>
      <ScenarioPopStreamingSlot>SCENARIO_POP_STREAMING_NORMAL</ScenarioPopStreamingSlot>
      <DefaultSpawningPreference>DSP_NORMAL</DefaultSpawningPreference>
      <DefaultRemoveRangeMultiplier value="1.000000" />
      <AllowCloseSpawning value="false" />
    </Item>
  </InitDatas>
</CPedModelInfo__InitDataList>
""";
    }
}
