using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Serialization;
using NLog.Targets;
using Sandbox.Definitions;
using Torch;
using Torch.Collections;
using VRage.Game;
using VRage.ObjectBuilders;

namespace Concealment
{
    public class Settings : ViewModel
    {
        private bool _enabled = true;
        private double _concealDistance = 75000;
        private int _concealInterval = 3600;
        private double _revealDistance = 50000;
        private int _revealInterval = 60;
        private bool _concealProduction;
        private bool _concealPirates;
        private bool _keepAliveAction;


		private double _dynamicConcealQueryInterval = 15;
        private double _dynamicConcealScanInterval = 2;

        /// <summary>
        /// Type of concealment constraint
        /// </summary>
        public enum DynamicConcealType
        {
            HostileCharacters = 0,
            NeutralCharacters,
            FriendlyCharacters,
            HostileGrids,
            NeutralGrids,

            // Must be last
            None
        }

        public class DynamicConcealSettings : ViewModel
        {
            private string _typeId;
            private string _subtypeId;
            private DynamicConcealType _concealType;
            private double _distance;

            /// <summary>
            /// Target type ID
            /// </summary>
            [XmlIgnore]
            public MyObjectBuilderType? TargetTypeId
            {
                get
                {
                    if (MyObjectBuilderType.TryParse(_typeId, out var type))
                        return type;
                    if (MyObjectBuilderType.TryParse("MyObjectBuilder_" + _typeId, out type))
                        return type;
                    return null;
                }
            }

            /// <summary>
            /// Subtype ID to target, or null to target all subtypes
            /// </summary>
            [XmlAttribute("TargetSubtype")]
            [DefaultValue(null)]
            public string TargetSubtypeId
            {
                get => _subtypeId;
                set
                {
                    _subtypeId = value;
                    OnPropertyChanged();
                }
            }

            /// <summary>
            /// The assembly qualified name of the <see cref="Target"/>
            /// </summary>
            [XmlAttribute("TargetType")]
            public string TargetTypeIdString
            {
                get => _typeId?.Replace("MyObjectBuilder_", "") ?? "null";
                set
                {
                    _typeId = value.Trim();
                    OnPropertyChanged();
                    // ReSharper disable once ExplicitCallerInfoArgument
                    OnPropertyChanged(nameof(TargetSubtypeIdOptions));
                }
            }

            /// <summary>
            /// Type of concealment
            /// </summary>
            [XmlAttribute("Type")]
            public DynamicConcealType DynamicConcealType
            {
                get => _concealType;
                set => SetValue(ref _concealType, value);
            }

            /// <summary>
            /// Distance to conceal at
            /// </summary>
            [XmlAttribute("Distance")]
            public double Distance
            {
                get => _distance;
                set => SetValue(ref _distance, value);
            }

            [XmlIgnore]
            public ICollection<string> TargetTypeIdOptions =>
                MyDefinitionManager.Static?.GetAllDefinitions()
                    .OfType<MyCubeBlockDefinition>()
                    .Select(x => x.Id.TypeId).Distinct()
                    .Select(x => x.ToString().Replace("MyObjectBuilder_", "")).ToList() ??
                new List<string>();

            [XmlIgnore]
            public ICollection<string> TargetSubtypeIdOptions =>
                MyDefinitionManager.Static?.GetAllDefinitions()
                    .OfType<MyCubeBlockDefinition>()
                    .Where(x => TargetTypeId.HasValue && x.Id.TypeId == TargetTypeId.Value)
                    .Select(x => x.Id.SubtypeName ?? "")
                    .ToList() ?? new List<string>();

            [XmlIgnore]
            public ICollection<DynamicConcealType> DynamicConcealTypeOptions =>
                ((DynamicConcealType[]) Enum.GetValues(typeof(DynamicConcealType)))
                .Where(x => x != DynamicConcealType.None).ToList();
        }

        [XmlIgnore]
        public MtObservableList<string> ExcludedSubtypes { get; } = new MtObservableList<string>();

        [XmlIgnore]
        public MtObservableList<DynamicConcealSettings> DynamicConcealment { get; } =
            new MtObservableList<DynamicConcealSettings>();

        public double DynamicConcealScanInterval
        {
            get => _dynamicConcealScanInterval;
            set => SetValue(ref _dynamicConcealScanInterval, value);
        }

        public double DynamicConcealQueryInterval
        {
            get => _dynamicConcealQueryInterval;
            set => SetValue(ref _dynamicConcealQueryInterval, value);
        }

        [XmlElement(nameof(DynamicConcealment))]
        public DynamicConcealSettings[] DynamicConcealmentSerial
        {
            get => DynamicConcealment.ToArray();
            set
            {
                DynamicConcealment.Clear();
                if (value != null)
                    foreach (var k in value)
                        DynamicConcealment.Add(k);
            }
        }

        [XmlElement(nameof(ExcludedSubtypes))]
        public string[] ExcludedSubtypesSerial
        {
            get => ExcludedSubtypes.ToArray();
            set
            {
                ExcludedSubtypes.Clear();
                if (value != null)
                    foreach (var k in value)
                        ExcludedSubtypes.Add(k);
            }
        }

        public bool Enabled
        {
            get => _enabled;
            set
            {
                _enabled = value;
                OnPropertyChanged();
            }
        }

        public int ConcealInterval
        {
            get => _concealInterval;
            set
            {
                _concealInterval = value;
                OnPropertyChanged();
            }
        }

        public int RevealInterval
        {
            get => _revealInterval;
            set
            {
                _revealInterval = value;
                OnPropertyChanged();
            }
        }

        public double ConcealDistance
        {
            get => _concealDistance;
            set
            {
                _concealDistance = value;
                OnPropertyChanged();
            }
        }

        public double RevealDistance
        {
            get => _revealDistance;
            set
            {
                _revealDistance = value;
                OnPropertyChanged();
            }
        }

        public bool ConcealProduction
        {
            get => _concealProduction;
            set
            {
                _concealProduction = value;
                OnPropertyChanged();
            }
        }

        public bool ConcealPirates
        {
            get => _concealPirates;
            set
            {
                _concealPirates = value;
                OnPropertyChanged();
            }
        }

		public bool RCKeepAliveAction
		{
			get => _keepAliveAction;
			set
			{
				_keepAliveAction = value;
				OnPropertyChanged();
			}
		}
	}
}