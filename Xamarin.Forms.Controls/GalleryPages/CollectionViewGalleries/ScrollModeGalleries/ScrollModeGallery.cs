﻿using System;
using System.Collections.Generic;
using System.Text;

namespace Xamarin.Forms.Controls.GalleryPages.CollectionViewGalleries.ScrollModeGalleries
{
	internal class ScrollModeGallery : ContentPage
	{
		public ScrollModeGallery()
		{
			var descriptionLabel =
					new Label { Text = "Scroll Mode Galleries", Margin = new Thickness(2, 2, 2, 2) };

			Title = "Scroll Mode Galleries";

			Content = new ScrollView
			{
				Content = new StackLayout
				{
					Children =
					{
						descriptionLabel,
						GalleryBuilder.NavButton("Keep Items in View", () =>
							new ScrollModeTestGallery(), Navigation),
						GalleryBuilder.NavButton("Keep Scroll Offset", () =>
							new ScrollModeTestGallery(), Navigation),
						GalleryBuilder.NavButton("Keep Last Item In View", () =>
							new ScrollModeTestGallery(), Navigation),
					}
				}
			};
		}
	}
}
