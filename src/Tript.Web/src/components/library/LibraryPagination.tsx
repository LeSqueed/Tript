// SPDX-License-Identifier: GPL-2.0-or-later

import { Button } from '../ui/controls';

interface LibraryPaginationProps {
  page: number;
  pageCount: number;
  onPageChange: (page: number) => void;
}

export function LibraryPagination({ page, pageCount, onPageChange }: LibraryPaginationProps) {
  return (
    <nav className="library-pagination" aria-label="Library pages">
      <Button
        variant="ghost"
        onClick={() => onPageChange(page - 1)}
        disabled={page <= 1}
        icon="chevronLeft"
        aria-label="Previous page"
      >
        Prev
      </Button>
      <span className="library-page-indicator" data-testid="library-page">
        Page {page} of {pageCount}
      </span>
      <Button
        variant="ghost"
        onClick={() => onPageChange(page + 1)}
        disabled={page >= pageCount}
        icon="chevronRight"
        aria-label="Next page"
      >
        Next
      </Button>
    </nav>
  );
}
