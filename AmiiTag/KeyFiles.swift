//
//  KeyFiles.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 7/7/20.
//  Copyright © 2020 Daniel Radtke. All rights reserved.
//

import Foundation

class KeyFiles {
    enum KeyError: Swift.Error {
        case FileError
    }
    
    fileprivate static var _lockedSecret: Data?
    fileprivate static var _unfixedInfo: Data?
    
    static var lockedSecret: Data? {
        if _lockedSecret != nil {
            return _lockedSecret!
        }
        
        guard
            let lockedSecretPath = Bundle.main.url(forResource: "locked-secret", withExtension: "bin"),
            let lockedSecret = try? Data(contentsOf: lockedSecretPath) else {
                return nil
        }
        
        _lockedSecret = lockedSecret
        return lockedSecret
    }
    
    static var unfixedInfo: Data? {
        if _unfixedInfo != nil {
            return _unfixedInfo!
        }
        
        guard
            let unfixedInfoPath = Bundle.main.url(forResource: "unfixed-info", withExtension: "bin"),
            let unfixedInfo = try? Data(contentsOf: unfixedInfoPath) else {
                return nil
        }
        
        _unfixedInfo = unfixedInfo
        return unfixedInfo
    }
    
    static let staticKey = TagKey(data: lockedSecret!)
    static let dataKey = TagKey(data: unfixedInfo!)
}
