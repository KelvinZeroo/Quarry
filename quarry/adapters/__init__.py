from .base import FetchFn, NoAdapterError, SiteAdapter, get_adapter
from .booru import BooruAdapter
from .erome import EromeAdapter
from .imagefap import ImageFapAdapter
from .pornpics import PornPicsAdapter

ADAPTERS = (PornPicsAdapter, ImageFapAdapter, EromeAdapter, BooruAdapter)

__all__ = [
    "ADAPTERS",
    "BooruAdapter",
    "EromeAdapter",
    "FetchFn",
    "ImageFapAdapter",
    "NoAdapterError",
    "PornPicsAdapter",
    "SiteAdapter",
    "get_adapter",
]
