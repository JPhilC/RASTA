using CommunityToolkit.Mvvm.ComponentModel;
using RASTA.Core.Config;
using RASTA.Infrastructure.Services;
using System;
using System.Collections.Generic;
using System.Text;

namespace RASTA.App.ViewModels
{
    public partial class UserOptionsViewModel : ObservableObject
    {
        private readonly UserOptionsService _service;

        public UserOptions Options { get; private set; }

        public UserOptionsViewModel(UserOptionsService service)
        {
            _service = service;
            Options = _service.Options;
            Options.PropertyChanged += Options_PropertyChanged;
        }

        private void Options_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            OnPropertyChanged(e.PropertyName);
        }

        public string CaptureFolder
        {
            get => Options.CaptureFolder;
            set
            {
                Options.CaptureFolder = value;
                OnPropertyChanged();
                Options.CaptureFolder = value;
            }
        }

        public string PlansFolder
        {
            get => Options.PlansFolder;
            set
            {
                Options.PlansFolder = value;
                OnPropertyChanged();
                Options.PlansFolder = value;
            }
        }

        public double DefaultCentreFrequencyHz
        {
            get => Options.DefaultCentreFrequencyHz;
            set
            {
                Options.DefaultCentreFrequencyHz = value;
                OnPropertyChanged();
                Options.DefaultCentreFrequencyHz = value;
            }
        }

        public double DefaultBandwidthHz
        {
            get => Options.DefaultBandwidthHz;
            set
            {
                Options.DefaultBandwidthHz = value;
                OnPropertyChanged();
                Options.DefaultBandwidthHz = value;
            }
        }

    }
}
