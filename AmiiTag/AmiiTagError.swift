//
//  AmiiTagError.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 2/12/21.
//  Copyright © 2021 Daniel Radtke. All rights reserved.
//

import Foundation

class AmiiTagError: Error {
    fileprivate var _localizedDescription: String = ""

    init(description: String) {
        self._localizedDescription = description
    }
}

extension AmiiTagError: LocalizedError {
    public var errorDescription: String? {
        return self._localizedDescription
    }
}
