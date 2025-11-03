#nullable enable

using System.Diagnostics.CodeAnalysis;
using T3.Core.DataTypes.Vector;
using T3.Editor.Gui.Styling;

namespace T3.Editor.Gui.Windows.AssetLib;

public static class AssetTypeRegistry
{
    public sealed class AssetType
    {
        public string Name;
        public List<int> ExtensionIds;
        public required List<Guid> PrimaryOperators;
        public required Color Color;
        public required Icon Icon;
        public int MatchingFileCount;

        internal AssetType(string name, List<int> extensionIds)
        {
            Name = name;
            ExtensionIds = extensionIds;
            foreach (var id in extensionIds)
            {
                _assetTypeForId[id] = this;
            }
        }
    }

    internal static void ClearMatchingFileCounts()
    {
        foreach (var a in _assetTypes)
        {
            a.MatchingFileCount = 0;
        }

        TotalAssetCount = 0;
    }

    internal static bool TryGetFromId(int id, [NotNullWhen(true)] out AssetType? type)
    {
        return _assetTypeForId.TryGetValue(id, out type);
    }

    private static List<AssetType> InitTypes()
    {
        return
            [
                new AssetType("Obj", [FileExtensionRegistry.GetId("obj")])
                    {
                        PrimaryOperators = [new Guid("be52b670-9749-4c0d-89f0-d8b101395227")], // LoadObj
                        Color = UiColors.ColorForGpuData,
                        Icon = Icon.FileGeometry,
                    },
                new AssetType("Gltf", [
                        FileExtensionRegistry.GetId("glb"),
                        FileExtensionRegistry.GetId("gltf"),
                    ])
                    {
                        PrimaryOperators =
                            [
                                new Guid("00618c91-f39a-44ea-b9d8-175c996460dc"), // LoadGltfScene
                                new Guid("92b18d2b-1022-488f-ab8e-a4dcca346a23"), // LoadGltf
                                // TODO: add more
                            ],
                        Color = UiColors.ColorForGpuData,
                        Icon = Icon.FileGeometry,
                    },

                new AssetType("Image", [
                        FileExtensionRegistry.GetId("png"),
                        FileExtensionRegistry.GetId("jpg"),
                        FileExtensionRegistry.GetId("jpeg"),
                        FileExtensionRegistry.GetId("bmp"),
                        FileExtensionRegistry.GetId("tga"),
                        FileExtensionRegistry.GetId("gif"),
                        FileExtensionRegistry.GetId("dds"),
                    ])
                    {
                        PrimaryOperators = [new Guid("0b3436db-e283-436e-ba85-2f3a1de76a9d")], // Load Image
                        Color = UiColors.ColorForTextures,
                        Icon = Icon.FileImage,
                    },

                new AssetType("Video", [
                        FileExtensionRegistry.GetId("mp4"),
                        FileExtensionRegistry.GetId("mov"),
                        FileExtensionRegistry.GetId("mpg"),
                        FileExtensionRegistry.GetId("mpeg"),
                        FileExtensionRegistry.GetId("m4v"),
                    ])
                    {
                        PrimaryOperators = [new Guid("914fb032-d7eb-414b-9e09-2bdd7049e049")], // PlayVideo
                        Color = UiColors.ColorForTextures,
                        Icon = Icon.FileVideo,
                    },
                
                new AssetType("Audio", [
                        FileExtensionRegistry.GetId("wav"),
                        FileExtensionRegistry.GetId("mp3"),
                        FileExtensionRegistry.GetId("ogg"),
                    ])
                    {
                        PrimaryOperators = [new Guid("c2b2758a-5b3e-465a-87b7-c6a13d3fba48")], // PlayAudioClip
                        Color = UiColors.ColorForValues,
                        Icon = Icon.FileAudio,
                    },

                
                new AssetType("Shader", [FileExtensionRegistry.GetId("hlsl")])
                    {
                        PrimaryOperators =
                            [
                                new Guid("a256d70f-adb3-481d-a926-caf35bd3e64c"), // ComputeShader
                                new Guid("646f5988-0a76-4996-a538-ba48054fd0ad"), // VertexShader
                                new Guid("f7c625da-fede-4993-976c-e259e0ee4985"), // PixelShader
                            ],
                        Color = UiColors.ColorForString,
                        Icon = Icon.FileShader,
                    },

                new AssetType("JSON", [FileExtensionRegistry.GetId("json")])
                    {
                        PrimaryOperators =
                            [
                                new Guid("5f71d2f8-98c8-4502-8f40-2ea4a1e18cca"), // ReadFile
                            ],
                        Color = UiColors.ColorForString,
                        Icon = Icon.FileDocument,
                    },
                
                new AssetType("TiXLFont", [FileExtensionRegistry.GetId("fnt")])
                    {
                        PrimaryOperators =
                            [
                                new Guid("fd31d208-12fe-46bf-bfa3-101211f8f497"), // Text
                            ],
                        Color = UiColors.ColorForCommands,
                        Icon = Icon.FileT3Font,
                    },
            ];
    }

    private static readonly Dictionary<int, AssetType> _assetTypeForId = [];
    private static List<AssetType> _assetTypes = InitTypes();
    public static  List<AssetType> AssetTypes => _assetTypes;
    internal static int TotalAssetCount;
}