using System;
using System.ComponentModel;
using System.Linq;
using Android.Content;
using Android.Graphics;
using Android.Support.V7.Widget;
using Android.Util;
using Android.Views;
using Android.Widget;
using Xamarin.Forms.Internals;
using Xamarin.Forms.Platform.Android.FastRenderers;
using AViewCompat = Android.Support.V4.View.ViewCompat;

namespace Xamarin.Forms.Platform.Android
{
	public class ItemsViewRenderer : RecyclerView, IVisualElementRenderer, IEffectControlProvider
	{
		readonly AutomationPropertiesProvider _automationPropertiesProvider;
		readonly EffectControlProvider _effectControlProvider;

		protected ItemsViewAdapter ItemsViewAdapter;

		int? _defaultLabelFor;
		bool _disposed;

		protected ItemsView ItemsView;

		IItemsLayout _layout;
		SnapManager _snapManager;

		EmptyViewAdapter _emptyViewAdapter;
		readonly DataChangeObserver _emptyCollectionObserver;
		readonly DataChangeObserver _itemsUpdateScrollObserver;
		ScrollHelper _scrollHelper;
		RecyclerView.ItemDecoration _itemDecoration;

		ScrollHelper ScrollHelper => _scrollHelper = _scrollHelper ?? new ScrollHelper(this);

		// RecyclerView doesn't provide a way to add scrollbars from code; you have to do it from a layout or style
		// So we're providing a collectionViewStyle with scrollbars pre-added here
		public ItemsViewRenderer(Context context) : base(new ContextThemeWrapper(context, Resource.Style.collectionViewStyle))
		{
			CollectionView.VerifyCollectionViewFlagEnabled(nameof(ItemsViewRenderer));

			_automationPropertiesProvider = new AutomationPropertiesProvider(this);
			_effectControlProvider = new EffectControlProvider(this);

			_emptyCollectionObserver = new DataChangeObserver(UpdateEmptyViewVisibility);
			_itemsUpdateScrollObserver = new DataChangeObserver(AdjustScrollForItemUpdate);

			// By default, we're going to leave the scroll bars off for now (this is the default for RecyclerView)
			// At some point, we'll be supporting turning these on/off (maybe controlling fading) on all the platforms,
			// and then we can move this into the appropriate property handlers
			// See https://github.com/xamarin/Xamarin.Forms/issues/6053
			VerticalScrollBarEnabled = false;
			HorizontalScrollBarEnabled = false;
		}

		// TODO hartez 2018/10/24 19:27:12 Region all the interface implementations	

		protected override void OnLayout(bool changed, int l, int t, int r, int b)
		{
			base.OnLayout(changed, l, t, r, b);
			AViewCompat.SetClipBounds(this, new Rect(0, 0, Width, Height));

			// After a direct (non-animated) scroll operation, we may need to make adjustments
			// to align the target item; if an adjustment is pending, execute it here.
			// (Deliberately checking the private member here rather than the property accessor; the accessor will
			// create a new ScrollHelper if needed, and there's no reason to do that until a Scroll is requested.)
			_scrollHelper?.AdjustScroll();
		}

		void IEffectControlProvider.RegisterEffect(Effect effect)
		{
			_effectControlProvider.RegisterEffect(effect);
		}

		public VisualElement Element => ItemsView;

		public event EventHandler<VisualElementChangedEventArgs> ElementChanged;

		public event EventHandler<PropertyChangedEventArgs> ElementPropertyChanged;

		SizeRequest IVisualElementRenderer.GetDesiredSize(int widthConstraint, int heightConstraint)
		{
			Measure(widthConstraint, heightConstraint);
			return new SizeRequest(new Size(MeasuredWidth, MeasuredHeight), new Size());
		}

		void IVisualElementRenderer.SetElement(VisualElement element)
		{
			if (element == null)
			{
				throw new ArgumentNullException(nameof(element));
			}

			if (!(element is ItemsView))
			{
				throw new ArgumentException($"{nameof(element)} must be of type {typeof(ItemsView).Name}");
			}

			var oldElement = ItemsView;
			var newElement = (ItemsView)element;

			TearDownOldElement(oldElement);
			SetUpNewElement(newElement);

			OnElementChanged(oldElement, newElement);

			// TODO hartez 2018/06/06 20:57:12 Find out what this does, and whether we really need it	
			element.SendViewInitialized(this);
		}

		void IVisualElementRenderer.SetLabelFor(int? id)
		{
			// TODO hartez 2018/06/06 20:58:54 Rethink whether we need to have _defaultLabelFor as a class member	
			if (_defaultLabelFor == null)
			{
				_defaultLabelFor = LabelFor;
			}

			LabelFor = (int)(id ?? _defaultLabelFor);
		}

		public VisualElementTracker Tracker { get; private set; }

		void IVisualElementRenderer.UpdateLayout()
		{
			Tracker?.UpdateLayout();
		}

		public global::Android.Views.View View => this;

		public ViewGroup ViewGroup => null;

		protected override void Dispose(bool disposing)
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;

			if (disposing)
			{
				_automationPropertiesProvider?.Dispose();
				Tracker?.Dispose();

				if (Element != null)
				{
					TearDownOldElement(Element as ItemsView);
				}

				if (Element != null)
				{
					if (Platform.GetRenderer(Element) == this)
					{
						Element.ClearValue(Platform.RendererProperty);
					}
				}
			}

			base.Dispose(disposing);
		}

		protected virtual LayoutManager SelectLayoutManager(IItemsLayout layoutSpecification)
		{
			switch (layoutSpecification)
			{
				case GridItemsLayout gridItemsLayout:
					return CreateGridLayout(gridItemsLayout);
				case ListItemsLayout listItemsLayout:
					var orientation = listItemsLayout.Orientation == ItemsLayoutOrientation.Horizontal
						? LinearLayoutManager.Horizontal
						: LinearLayoutManager.Vertical;

					return new LinearLayoutManager(Context, orientation, false);
			}

			// Fall back to plain old vertical list
			// TODO hartez 2018/08/30 19:34:36 Log a warning when we have to fall back because of an unknown layout	
			return new LinearLayoutManager(Context);
		}

		GridLayoutManager CreateGridLayout(GridItemsLayout gridItemsLayout)
		{
			return new GridLayoutManager(Context, gridItemsLayout.Span,
				gridItemsLayout.Orientation == ItemsLayoutOrientation.Horizontal
					? LinearLayoutManager.Horizontal
					: LinearLayoutManager.Vertical,
				false);
		} 

		void OnElementChanged(ItemsView oldElement, ItemsView newElement)
		{
			ElementChanged?.Invoke(this, new VisualElementChangedEventArgs(oldElement, newElement));
			EffectUtilities.RegisterEffectControlProvider(this, oldElement, newElement);
		}

		protected virtual void OnElementPropertyChanged(object sender, PropertyChangedEventArgs changedProperty)
		{
			ElementPropertyChanged?.Invoke(this, changedProperty);

			// TODO hartez 2018/10/24 10:41:55 If the ItemTemplate changes from set to null, we need to make sure to clear the recyclerview pool	

			if (changedProperty.Is(ItemsView.ItemsSourceProperty))
			{
				UpdateItemsSource();
			}
			else if (changedProperty.Is(VisualElement.BackgroundColorProperty))
			{
				UpdateBackgroundColor();
			}
			else if (changedProperty.Is(VisualElement.FlowDirectionProperty))
			{
				UpdateFlowDirection();
			}
			else if (changedProperty.IsOneOf(ItemsView.EmptyViewProperty, ItemsView.EmptyViewTemplateProperty))
			{
				UpdateEmptyView();
			}
			else if (changedProperty.Is(ItemsView.ItemSizingStrategyProperty))
			{
				UpdateAdapter();
			}
			else if (changedProperty.Is(ItemsView.ItemsUpdatingScrollModeProperty))
			{
				UpdateItemsUpatingScrollMode();
			}
		}

		protected virtual void UpdateItemsSource()
		{
			if (ItemsView == null)
			{
				return;
			}

			// Stop watching the old adapter to see if it's empty (if we are watching)
			Unwatch(ItemsViewAdapter ?? GetAdapter());

			UpdateAdapter();

			// Set up any properties which require observing data changes in the adapter
			UpdateItemsUpatingScrollMode();
			UpdateEmptyView();
		}

		protected virtual void UpdateAdapter()
		{
			var oldItemViewAdapter = ItemsViewAdapter;

			ItemsViewAdapter = new ItemsViewAdapter(ItemsView);

			SwapAdapter(ItemsViewAdapter, true);

			oldItemViewAdapter?.Dispose();
		}

		void Unwatch(Adapter adapter)
		{
			if (_watchingForEmpty && adapter != null && _dataChangeViewObserver != null)
			{
				adapter.UnregisterAdapterDataObserver(_dataChangeViewObserver);
			}

			_watchingForEmpty = false;
		}

		// TODO hartez 2018/10/24 19:25:14 I don't like these method names; too generic 	
		// TODO hartez 2018/11/05 22:37:42 Also, thinking all the EmptyView stuff should be moved to a helper	
		void Watch(Adapter adapter)
		{
			if (_watchingForEmpty)
			{
				return;
			}

			if (_dataChangeViewObserver == null)
			{
				_dataChangeViewObserver = new DataChangeObserver(UpdateEmptyViewVisibility);
			}

			adapter.RegisterAdapterDataObserver(_dataChangeViewObserver);
			_watchingForEmpty = true;
		}

		protected virtual void SetUpNewElement(ItemsView newElement)
		{
			if (newElement == null)
			{
				ItemsView = null;
				return;
			}

			ItemsView = newElement;

			ItemsView.PropertyChanged += OnElementPropertyChanged;

			// TODO hartez 2018/06/06 20:49:14 Review whether we can just do this in the constructor	
			if (Tracker == null)
			{
				Tracker = new VisualElementTracker(this);
			}

			this.EnsureId();

			UpdateItemsSource();

			_layout = ItemsView.ItemsLayout;
			SetLayoutManager(SelectLayoutManager(_layout));

			UpdateSnapBehavior();
			UpdateBackgroundColor();
			UpdateFlowDirection();
			UpdateItemSpacing();

			// Keep track of the ItemsLayout's property changes
			if (_layout != null)
			{
				_layout.PropertyChanged += LayoutOnPropertyChanged;
			}

			// Listen for ScrollTo requests
			ItemsView.ScrollToRequested += ScrollToRequested;
		}

		protected virtual void TearDownOldElement(ItemsView oldElement)
		{
			if (oldElement == null)
			{
				return;
			}

			// Stop listening for layout property changes
			if (_layout != null)
			{
				_layout.PropertyChanged -= LayoutOnPropertyChanged;
			}

			// Stop listening for property changes
			oldElement.PropertyChanged -= OnElementPropertyChanged;

			// Stop listening for ScrollTo requests
			oldElement.ScrollToRequested -= ScrollToRequested;

			if (ItemsViewAdapter != null)
			{
				// Stop watching for empty items or scroll adjustments
				_emptyCollectionObserver.Stop(ItemsViewAdapter);
				_itemsUpdateScrollObserver.Stop(ItemsViewAdapter);
			}

			// Unhook whichever adapter is active
			SetAdapter(null);
			
			if (_emptyViewAdapter != null)
			{
				_emptyViewAdapter.Dispose();
			}
			
			if (_itemsUpdateScrollObserver != null)
			{
				_itemsUpdateScrollObserver.Dispose();
			}

			if (ItemsViewAdapter != null)
			{
				ItemsViewAdapter.Dispose();
			}

			if (_snapManager != null)
			{
				_snapManager.Dispose();
				_snapManager = null;
			}

			if (_itemDecoration != null)
			{
				RemoveItemDecoration(_itemDecoration);
			}
		}

		protected virtual void LayoutOnPropertyChanged(object sender, PropertyChangedEventArgs propertyChanged)
		{
			if (propertyChanged.Is(GridItemsLayout.SpanProperty))
			{
				if (GetLayoutManager() is GridLayoutManager gridLayoutManager)
				{
					gridLayoutManager.SpanCount = ((GridItemsLayout)_layout).Span;
				}
			}
			else if (propertyChanged.IsOneOf(ItemsLayout.SnapPointsTypeProperty, ItemsLayout.SnapPointsAlignmentProperty))
			{
				UpdateSnapBehavior();
			}
			else if (propertyChanged.IsOneOf(ListItemsLayout.ItemSpacingProperty,
				GridItemsLayout.HorizontalItemSpacingProperty, GridItemsLayout.VerticalItemSpacingProperty))
			{
				UpdateItemSpacing();
			}
		}

		protected virtual void UpdateSnapBehavior()
		{
			if (_snapManager == null)
			{
				_snapManager = new SnapManager(ItemsView, this);
			}

			_snapManager.UpdateSnapBehavior();
		}

		// TODO hartez 2018/08/09 09:30:17 Package up background color and flow direction providers so we don't have to re-implement them here	
		protected virtual void UpdateBackgroundColor(Color? color = null)
		{
			if (Element == null)
			{
				return;
			}

			SetBackgroundColor((color ?? Element.BackgroundColor).ToAndroid());
		}

		protected virtual void UpdateFlowDirection()
		{
			if (Element == null)
			{
				return;
			}

			this.UpdateFlowDirection(Element);

			ReconcileFlowDirectionAndLayout();
		}

		protected virtual void UpdateEmptyView()
		{
			if (ItemsViewAdapter == null || ItemsView == null)
			{
				return;
			}

			var emptyView = ItemsView?.EmptyView;
			var emptyViewTemplate = ItemsView?.EmptyViewTemplate;

			if (emptyView != null || emptyViewTemplate != null)
			{
				if (_emptyViewAdapter == null)
				{
					_emptyViewAdapter = new EmptyViewAdapter(ItemsView);
				}

				_emptyViewAdapter.EmptyView = emptyView;
				_emptyViewAdapter.EmptyViewTemplate = emptyViewTemplate;
				_emptyCollectionObserver.Start(ItemsViewAdapter);
			}
			else
			{
				_emptyCollectionObserver.Stop(ItemsViewAdapter);
			}

			UpdateEmptyViewVisibility();
		}

		protected virtual void UpdateItemsUpatingScrollMode()
		{
			if (ItemsViewAdapter == null || ItemsView == null)
			{
				return;
			}

			if (ItemsView.ItemsUpdatingScrollMode == ItemsUpdatingScrollMode.KeepItemsInView)
			{
				// Keeping the current items in view is the default, so we don't need to watch for data changes
				_itemsUpdateScrollObserver.Stop(ItemsViewAdapter);
			}
			else
			{
				_itemsUpdateScrollObserver.Start(ItemsViewAdapter);
			}
		}

		protected virtual void ReconcileFlowDirectionAndLayout()
		{
			if (!(GetLayoutManager() is LinearLayoutManager linearLayoutManager))
			{
				return;
			}

			if (linearLayoutManager.CanScrollVertically())
			{
				return;
			}

			var effectiveFlowDirection = ((IVisualElementController)Element).EffectiveFlowDirection;

			if (effectiveFlowDirection.IsRightToLeft() && !linearLayoutManager.ReverseLayout)
			{
				linearLayoutManager.ReverseLayout = true;
				return;
			}

			if (effectiveFlowDirection.IsLeftToRight() && linearLayoutManager.ReverseLayout)
			{
				linearLayoutManager.ReverseLayout = false;
			}
		}

		protected virtual int DeterminePosition(ScrollToRequestEventArgs args)
		{
			if (args.Mode == ScrollToMode.Position)
			{
				// TODO hartez 2018/08/28 15:40:03 Need to handle group indices here as well	
				return args.Index;
			}

			return ItemsViewAdapter.GetPositionForItem(args.Item);
		}

		protected virtual void UpdateItemSpacing()
		{
			if (_layout == null)
			{
				return;
			}

			if (_itemDecoration != null)
			{
				RemoveItemDecoration(_itemDecoration);
			}

			_itemDecoration = new SpacingItemDecoration(_layout);
			AddItemDecoration(_itemDecoration);
		}

		void ScrollToRequested(object sender, ScrollToRequestEventArgs args)
		{
			ScrollTo(args);
		}

		protected virtual void ScrollTo(ScrollToRequestEventArgs args)
		{
			var position = DeterminePosition(args);

			if (args.IsAnimated)
			{
				ScrollHelper.AnimateScrollToPosition(position, args.ScrollToPosition);
			}
			else
			{
				ScrollHelper.JumpScrollToPosition(position, args.ScrollToPosition);
			}
		}

		internal void UpdateEmptyViewVisibility()
		{
			if (ItemsViewAdapter == null)
			{
				return;
			}

			var showEmptyView = ItemsView?.EmptyView != null && ItemsViewAdapter.ItemCount == 0;

			if (showEmptyView)
			{
				SwapAdapter(_emptyViewAdapter, true);

				// TODO hartez 2018/10/24 17:34:36 If this works, cache this layout manager as _emptyLayoutManager	
				// TODO Also, cache whether the empty view is visible so we don't have to call SwapAdapter when count goes from >0 to also >0
				SetLayoutManager(new LinearLayoutManager(Context));
			}
			else
			{
				SwapAdapter(ItemsViewAdapter, true);
				SetLayoutManager(SelectLayoutManager(_layout));
			}
		}

		internal void AdjustScrollForItemUpdate()
		{
			if (ItemsView.ItemsUpdatingScrollMode == ItemsUpdatingScrollMode.KeepLastItemInView)
			{
				ScrollTo(new ScrollToRequestEventArgs(ItemsViewAdapter.ItemCount, 0,
					Xamarin.Forms.ScrollToPosition.MakeVisible, true));
			}
			else if (ItemsView.ItemsUpdatingScrollMode == ItemsUpdatingScrollMode.KeepScrollOffset)
			{
				ScrollHelper.UndoNextScrollAdjustment();
			}
		}
	}
}