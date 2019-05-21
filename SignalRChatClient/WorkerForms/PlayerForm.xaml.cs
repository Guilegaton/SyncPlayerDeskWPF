﻿using Microsoft.AspNetCore.SignalR.Client;
using PLL.Services;
using SignalRChatClient.WorkerForms;
using SyncPlayer.Helpers;
using SyncPlayer.Models;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace SyncPlayer
{
    public partial class PlayerForm : Window
    {
        private HubConnection _connection;
        private List<string> _roomUsers;
        private bool _readyToPLay = false;
        private bool _isHost = false;
        private ConnectToRoom _connectToRoom;
        private List<Media> _playlist;
        private MediaLoadService _mediaLoadService;
        private AppSettingsReader _appSettingsReader;
        private Room _room;

        public PlayerForm(Room room, IEnumerable<Media> playlist, bool isHost)
        {
            InitializeComponent();
            _room = room;
            _isHost = isHost;
            _roomUsers = new List<string>();
            _playlist = playlist.ToList();
            _appSettingsReader = new AppSettingsReader();
            _mediaLoadService = new MediaLoadService((string)_appSettingsReader.GetValue("BlobUrl", typeof(string)), (string)_appSettingsReader.GetValue("ContainerName", typeof(string)));

            foreach (var media in _playlist)
            {
                PlayListLB.Items.Add(media.Name);
            }

            this.Title = room.Name;
            this.Closing += PlayerForm_FormClosing;

            mePlayer.Source = new Uri(playlist.FirstOrDefault().FileName);
            mePlayer.Volume = PlayerVolume.Value;
            mePlayer.MediaEnded += MediaEnd;

            DispatcherTimer timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += timer_Tick;
            timer.Start();


            _connection = new HubConnectionBuilder()
                .WithUrl((string)_appSettingsReader.GetValue("ServerHost", typeof(string)), options =>
                {
                    options.AccessTokenProvider = async () => SessionHelper.ActiveUser.AccessToken;
                })
                .Build();

            #region snippet_ClosedRestart

            _connection.Closed += async (error) =>
            {
                await Task.Delay(new Random().Next(0, 5) * 1000);
                await _connection.StartAsync();
            };

            #endregion snippet_ClosedRestart

            RegisterListeners();

            try
            {
                Task.Run(async () =>
                {
                    await _connection.StartAsync();
                    if (!isHost)
                    {
                        await _connection.InvokeAsync("CheckMedia", _playlist);
                    }
                });
            }
            catch (Exception ex)
            {
                messagesList.Items.Add(ex.Message);
            }
        }

        private void UpdateUserList()
        {
            this.Dispatcher.Invoke(() =>
            {
                UserListLB.Items.Clear();
                foreach (var user in _roomUsers)
                {
                    UserListLB.Items.Add(user);
                }
            });
        }

        private void RegisterListeners()
        {

            _connection.On<string>("UserDisconect", (username) =>
            {
                _roomUsers.Remove(username);
                UpdateUserList();
            });
            _connection.On<string>("UserConnected", (username) =>
            {
                _roomUsers.Add(username);
                _readyToPLay = false;
                mePlayer.Pause();
                btnPlay.IsEnabled = false;
                UpdateUserList();
            });
            _connection.On("RoomClosed", async () =>
            {
                await _connection.StopAsync();
                if (_connectToRoom == null)
                {
                    _connectToRoom = new ConnectToRoom();
                    _connectToRoom.Show();
                    this.Close();
                }
            });
            _connection.On<string, IEnumerable<string>>("DownloadMedia", async (fileName, chunks) =>
            {
                var folderName = await _mediaLoadService.DownloadFileAsync(fileName, chunks, _room.UniqName);
                _mediaLoadService.MergeFile(folderName, _room.PlaylistPath);
                await _connection.SendAsync("MediaDownloaded");
            });
            _connection.On<Media>("NextMedia", model =>
            {
                var media = GetMedia(model);
                mePlayer.Stop();
                mePlayer.Source = new Uri(media.FileName);
                mePlayer.Play();
            });
            _connection.On<string, string>("Receive", AddTextToChat);
            _connection.On<TimeSpan>("Play", (currentTrackTime) =>
            {
                if (_readyToPLay)
                {
                    mePlayer.Play();
                }
            });
            _connection.On("Pause", () => mePlayer.Pause());
            _connection.On("Stop", () => mePlayer.Stop());
            _connection.On("ReadyToPlay", () =>
            {
                _readyToPLay = true;
            });

            if (_isHost)
            {
                btnPause.Visibility = Visibility.Visible;
                btnPlay.Visibility = Visibility.Visible;
                btnStop.Visibility = Visibility.Visible;
                btnPlay.IsEnabled = false;

                _connection.On<IEnumerable<Media>, string>("RequireMedia", async (models, id) =>
                {
                    foreach (var media in models)
                    {
                        var uploadMedia = GetMedia(media);

                        var result = await _mediaLoadService.UploadFileAsync(uploadMedia.FileName, _room.UniqName);
                        await _connection.SendAsync("UploadMedia", id, uploadMedia.Name, result);
                    }
                });
                _connection.On("RequireNextMedia", () =>
                {
                    _playlist.Remove(_playlist.FirstOrDefault(media => media.FileName == mePlayer.Source.LocalPath));
                    var nextMedia = _playlist.FirstOrDefault();
                    _connection.SendAsync("SetNextMedia", nextMedia);
                });
            }
        }

        private void AddTextToChat(string userName, string message)
        {
            this.Dispatcher.Invoke(() =>
            {
                var newMessage = $"{userName}: {message}";
                messagesList.Items.Add(newMessage);
            });
        }

        private async void sendButton_Click(object sender, RoutedEventArgs e)
        {
            #region snippet_ErrorHandling

            try
            {
                #region snippet_InvokeAsync

                await _connection.InvokeAsync("Message",
                     messageTextBox.Text);

                #endregion snippet_InvokeAsync
            }
            catch (Exception ex)
            {
                messagesList.Items.Add(ex.Message);
            }

            #endregion snippet_ErrorHandling
        }

        private async void PlayerForm_FormClosing(object sender, EventArgs e)
        {
            await _connection.StopAsync();
        }

        private async void DisconnectBTN_Click(object sender, RoutedEventArgs e)
        {
            await _connection.StopAsync();
            if (_connectToRoom == null)
            {
                _connectToRoom = new ConnectToRoom();
                _connectToRoom.Show();
                this.Close();
            }
        }

        private void timer_Tick(object sender, EventArgs e)
        {
            if (mePlayer.Source != null)
            {
                if (mePlayer.NaturalDuration.HasTimeSpan)
                    lblStatus.Content = String.Format("{0} / {1}", mePlayer.Position.ToString(@"mm\:ss"), mePlayer.NaturalDuration.TimeSpan.ToString(@"mm\:ss"));
            }
            else
                lblStatus.Content = "No file selected...";
        }

        private void btnPlay_Click(object sender, RoutedEventArgs e)
        {
            _connection.SendAsync("Play", mePlayer.Position);
        }

        private void btnPause_Click(object sender, RoutedEventArgs e)
        {
            _connection.SendAsync("Pause", mePlayer.Position);
        }

        private void btnStop_Click(object sender, RoutedEventArgs e)
        {
            _connection.SendAsync("Stop", mePlayer.Position);
        }

        private void PlayerVolume_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            mePlayer.Volume = PlayerVolume.Value;
        }

        private void MediaEnd(object sender, EventArgs e)
        {
            if (_playlist.Remove(_playlist.FirstOrDefault(media => mePlayer.Source.LocalPath == media.FileName)) && _playlist.Count > 0)
            {
                PlayListLB.Items.Clear();
                foreach (var media in _playlist)
                {
                    PlayListLB.Items.Add(media.Name);
                }
                _connection.SendAsync("TrackEnded");
            }
        }

        private Media GetMedia(Media externalMedia)
        {
            var result = _playlist.FirstOrDefault(media => {
                var sum = 0;
                if (media.Album == externalMedia.Album)
                    sum++;
                if (media.BitRate == externalMedia.BitRate)
                    sum++;
                if (media.Description == externalMedia.Description)
                    sum++;
                if (media.Duration == externalMedia.Duration)
                    sum++;
                if (media.EndPosition == externalMedia.EndPosition)
                    sum++;
                if (media.Genre == externalMedia.Genre)
                    sum++;
                if (media.MimeType == externalMedia.MimeType)
                    sum++;
                if (media.Name == externalMedia.Name)
                    sum++;
                if (media.Rate == externalMedia.Rate)
                    sum++;
                if (media.Singler == externalMedia.Singler)
                    sum++;
                if (media.StartPosition == externalMedia.StartPosition)
                    sum++;
                if (media.Type == externalMedia.Type)
                    sum++;
                return sum >= 10;
            });

            return result;
        }
    }
}