using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Foundation;
using MobileCoreServices;
using Photos;
using PhotosUI;
using UIKit;

namespace Xamarin.Essentials
{
    public static partial class MediaPicker
    {
        static UIImagePickerController picker;
        static PHPickerViewController photoPicker;

        static bool PlatformIsCaptureSupported
            => UIImagePickerController.IsSourceTypeAvailable(UIImagePickerControllerSourceType.Camera);

        static Task<List<FileResult>> PlatformPickPhotosAsync(MediaPickerOptions options)
            => PhotosAsync(options, true);

        static Task<FileResult> PlatformPickPhotoAsync(MediaPickerOptions options)
            => PhotoAsync(options, true, true);

        static Task<FileResult> PlatformCapturePhotoAsync(MediaPickerOptions options)
            => PhotoAsync(options, true, false);

        static Task<FileResult> PlatformPickVideoAsync(MediaPickerOptions options)
            => PhotoAsync(options, false, true);

        static Task<FileResult> PlatformCaptureVideoAsync(MediaPickerOptions options)
            => PhotoAsync(options, false, false);

        static async Task<List<FileResult>> PhotosAsync(MediaPickerOptions options, bool photo)
        {
            await Task.Delay(1);

            var sourceType = UIImagePickerControllerSourceType.PhotoLibrary;
            var mediaType = photo ? UTType.Image : UTType.Movie;

            if (!UIImagePickerController.IsSourceTypeAvailable(sourceType))
                throw new FeatureNotSupportedException();
            if (!UIImagePickerController.AvailableMediaTypes(sourceType).Contains(mediaType))
                throw new FeatureNotSupportedException();

            // microphone only needed if video will be captured
            if (!photo)
                await Permissions.EnsureGrantedAsync<Permissions.Microphone>();

            // Check if picking existing or not and ensure permission accordingly as they can be set independently from each other
            if (!Platform.HasOSVersion(11, 0))
                await Permissions.EnsureGrantedAsync<Permissions.Photos>();

            var vc = Platform.GetCurrentViewController(true);

            var filter = photo ? PHPickerFilter.ImagesFilter : PHPickerFilter.VideosFilter;

            var config = new PHPickerConfiguration(PHPhotoLibrary.SharedPhotoLibrary)
            {
                Filter = filter,
                PreferredAssetRepresentationMode = PHPickerConfigurationAssetRepresentationMode.Current,
                SelectionLimit = 0,
            };

            photoPicker = new PHPickerViewController(config);

            if (DeviceInfo.Idiom == DeviceIdiom.Tablet &&
                photoPicker.PopoverPresentationController != null &&
                vc.View != null)
                photoPicker.PopoverPresentationController.SourceRect = vc.View.Bounds;

            var tcs = new TaskCompletionSource<List<FileResult>>(photoPicker);
            photoPicker.Delegate = new PPD
            {
                CompletedHandler = res =>
                    tcs.TrySetResult(PickerResultsToMediaFile(res))
            };

            await vc.PresentViewControllerAsync(photoPicker, true);

            var result = await tcs.Task;

            await vc.DismissViewControllerAsync(true);

            photoPicker?.Dispose();
            photoPicker = null;

            return result;
        }

        static async Task<FileResult> PhotoAsync(MediaPickerOptions options, bool photo, bool pickExisting)
        {
            var sourceType = pickExisting ? UIImagePickerControllerSourceType.PhotoLibrary : UIImagePickerControllerSourceType.Camera;
            var mediaType = photo ? UTType.Image : UTType.Movie;

            if (!UIImagePickerController.IsSourceTypeAvailable(sourceType))
                throw new FeatureNotSupportedException();
            if (!UIImagePickerController.AvailableMediaTypes(sourceType).Contains(mediaType))
                throw new FeatureNotSupportedException();

            // microphone only needed if video will be captured
            if (!photo && !pickExisting)
                await Permissions.EnsureGrantedAsync<Permissions.Microphone>();

            // Check if picking existing or not and ensure permission accordingly as they can be set independently from each other
            if (pickExisting && !Platform.HasOSVersion(11, 0))
                await Permissions.EnsureGrantedAsync<Permissions.Photos>();

            if (!pickExisting)
                await Permissions.EnsureGrantedAsync<Permissions.Camera>();

            var vc = Platform.GetCurrentViewController(true);

            picker = new UIImagePickerController();
            picker.SourceType = sourceType;
            picker.MediaTypes = new string[] { mediaType };
            picker.AllowsEditing = false;
            if (!photo && !pickExisting)
            {
                picker.CameraCaptureMode = UIImagePickerControllerCameraCaptureMode.Video;
                picker.VideoQuality = UIImagePickerControllerQualityType.High;
            }

            if (!string.IsNullOrWhiteSpace(options?.Title))
                picker.Title = options.Title;

            if (DeviceInfo.Idiom == DeviceIdiom.Tablet && picker.PopoverPresentationController != null && vc.View != null)
                picker.PopoverPresentationController.SourceRect = vc.View.Bounds;

            var tcs = new TaskCompletionSource<FileResult>(picker);
            picker.Delegate = new PhotoPickerDelegate
            {
                CompletedHandler = async info =>
                {
                    GetFileResult(info, tcs);
                    await vc.DismissViewControllerAsync(true);
                }
            };

            if (picker.PresentationController != null)
            {
                picker.PresentationController.Delegate = new Platform.UIPresentationControllerDelegate
                {
                    DismissHandler = () => GetFileResult(null, tcs)
                };
            }

            await vc.PresentViewControllerAsync(picker, true);

            var result = await tcs.Task;

            picker?.Dispose();
            picker = null;

            return result;
        }

        static void GetFileResult(NSDictionary info, TaskCompletionSource<FileResult> tcs)
        {
            try
            {
                tcs.TrySetResult(DictionaryToMediaFile(info));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }

        static FileResult DictionaryToMediaFile(NSDictionary info)
        {
            if (info == null)
                return null;

            PHAsset phAsset = null;
            NSUrl assetUrl = null;

            if (Platform.HasOSVersion(11, 0))
            {
                assetUrl = info[UIImagePickerController.ImageUrl] as NSUrl;

                // Try the MediaURL sometimes used for videos
                if (assetUrl == null)
                    assetUrl = info[UIImagePickerController.MediaURL] as NSUrl;

                if (assetUrl != null)
                {
                    if (!assetUrl.Scheme.Equals("assets-library", StringComparison.InvariantCultureIgnoreCase))
                        return new UIDocumentFileResult(assetUrl);

                    phAsset = info.ValueForKey(UIImagePickerController.PHAsset) as PHAsset;
                }
            }

            if (phAsset == null)
            {
                assetUrl = info[UIImagePickerController.ReferenceUrl] as NSUrl;

                if (assetUrl != null)
                    phAsset = PHAsset.FetchAssets(new NSUrl[] { assetUrl }, null)?.LastObject as PHAsset;
            }

            if (phAsset == null || assetUrl == null)
            {
                var img = info.ValueForKey(UIImagePickerController.OriginalImage) as UIImage;

                if (img != null)
                    return new UIImageFileResult(img);
            }

            if (phAsset == null || assetUrl == null)
                return null;

            string originalFilename;

            if (Platform.HasOSVersion(9, 0))
                originalFilename = PHAssetResource.GetAssetResources(phAsset).FirstOrDefault()?.OriginalFilename;
            else
                originalFilename = phAsset.ValueForKey(new NSString("filename")) as NSString;

            return new PHAssetFileResult(assetUrl, phAsset, originalFilename);
        }

        class PPD : PHPickerViewControllerDelegate
        {
            public Action<PHPickerResult[]> CompletedHandler { get; set; }

            public override void DidFinishPicking(PHPickerViewController picker, PHPickerResult[] results) =>
                CompletedHandler?.Invoke(results?.Length > 0 ? results : null);
        }

        class PhotoPickerDelegate : UIImagePickerControllerDelegate
        {
            public Action<NSDictionary> CompletedHandler { get; set; }

            public override void FinishedPickingMedia(UIImagePickerController picker, NSDictionary info) =>
                CompletedHandler?.Invoke(info);

            public override void Canceled(UIImagePickerController picker) =>
                CompletedHandler?.Invoke(null);
        }

        class PhotoPickerPresentationControllerDelegate : UIAdaptivePresentationControllerDelegate
        {
            public Action<NSDictionary> CompletedHandler { get; set; }

            public override void DidDismiss(UIPresentationController presentationController) =>
                CompletedHandler?.Invoke(null);
        }

        static List<FileResult> PickerResultsToMediaFile(PHPickerResult[] results)
        {
            var ret = new List<FileResult>();

            if (results == null || results.Length == 0)
                return ret;

            foreach (var r in results)
            {
                if (r.ItemProvider == null)
                    continue;

                ret.Add(new PHPickerFileResult(r.ItemProvider));
            }

            return ret;
        }

        class PHPickerFileResult : FileResult
        {
            readonly string identifier;
            readonly NSItemProvider provider;

            internal PHPickerFileResult(NSItemProvider provider)
            {
                this.provider = provider;
                var identifiers = provider?.RegisteredTypeIdentifiers;

                identifier = (identifiers?.Any(i => i.StartsWith(UTType.LivePhoto)) ?? false)
                    && (identifiers?.Contains(UTType.JPEG) ?? false)
                    ? identifiers?.FirstOrDefault(i => i == UTType.JPEG)
                    : identifiers?.FirstOrDefault();

                if (string.IsNullOrWhiteSpace(identifier))
                    return;
                FileName = FullPath
                    = $"{provider?.SuggestedName}.{GetTag(identifier, UTType.TagClassFilenameExtension)}";
            }

            internal override async Task<Stream> PlatformOpenReadAsync()
                => (await provider?.LoadDataRepresentationAsync(identifier))?.AsStream();

            protected internal static string GetTag(string identifier, string tagClass)
            => UTType.CopyAllTags(identifier, tagClass)?.FirstOrDefault();
        }
    }
}
